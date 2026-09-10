// libsdlwinfix — corrective shim for the SDL foreign-window embed defect.
//
// OBSERVE (SBOX_SDLWINFIX=observe) or CORRECT (SBOX_SDLWINFIX=1) the answers
// SDL_GetWindowSize / SDL_GetWindowPosition give for an SDL window that Qt
// embeds as a foreign child without ever forwarding geometry. Qt reparents
// the SDL X window under a container; SDL keeps answering with the frozen
// creation size and (0,0), so Screen.Size and the engine cursor origin are
// wrong. When the window is an embedded X child (parent != root) the shim
// answers from the live X parent geometry instead. Top-level windows and
// every failure path pass through untouched (fail open to real values).
//
// v2: persistent X connection (v1 opened+closed one per query and the churn
// degraded under per-frame load -> mass fail-open), per-reason fail counters,
// per-window change-gated fail/move lines, SDL_PollEvent histogram (proves
// whether the engine's mouse feed is alive while answers are faked).
//
// Build: gcc -shared -fPIC -O2 -o libsdlwinfix.so sdlwinfix.c -lX11 -lpthread -ldl
#define _GNU_SOURCE
#include <X11/Xlib.h>
#include <dlfcn.h>
#include <fcntl.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

typedef void SDL_Window;
typedef uint32_t SDL_PropertiesID;

#define TITLE_LEN 64
#define MAX_WINDOWS 32

typedef struct
{
	int known;
	SDL_Window *ptr;
	unsigned id;
	char title[TITLE_LEN];
	// Last answered values (change-gated ans lines).
	int lw;
	int lh;
	int lx;
	int ly;
	int lsrc; // 0=none yet, 1=real, 2=parent
	// Last known-good parent geometry (move detection).
	int have_parent;
	int ppx;
	int ppy;
	int ppw;
	int pph;
	// Last REAL (unfaked) size SDL believes - the fossil coordinate space.
	int rw;
	int rh;
	int topo_done;
	// Last fail reason logged (change-gated fail lines, 0=ok).
	int lfail;
	// Query count (identifies the window the engine sizes/cursors from).
	unsigned nq;
} WindowEntry;

static WindowEntry windows[MAX_WINDOWS];

// Fail reasons (also used for change-gated fail lines).
#define FR_OK 0
#define FR_SYM 1 // dlsym failed
#define FR_PROPS 2 // no SDL properties
#define FR_XID 3 // no X11 xid property
#define FR_DPY 4 // no X display (connection down)
#define FR_QUERY 5 // XQueryTree failed
#define FR_GEO 6 // XGetGeometry failed

static SDL_Window *(*real_CreateWindowWithProperties)( SDL_PropertiesID ) = 0;
static unsigned (*real_GetWindowID)( SDL_Window * ) = 0;
static const char *(*real_GetWindowTitle)( SDL_Window * ) = 0;
static int (*real_GetWindowSize)( SDL_Window *, int *, int * ) = 0;
static int (*real_GetWindowPosition)( SDL_Window *, int *, int * ) = 0;
static SDL_PropertiesID (*real_GetWindowProperties)( SDL_Window * ) = 0;
static long long (*real_GetNumberProperty)( SDL_PropertiesID, const char *, long long ) = 0;
static int (*real_PollEvent)( void * ) = 0;

static unsigned long n_create = 0;
static unsigned long n_getsize = 0;
static unsigned long n_getposition = 0;
static unsigned long n_faked = 0;
static unsigned long n_top = 0;
static unsigned long n_f_sym = 0;
static unsigned long n_f_props = 0;
static unsigned long n_f_xid = 0;
static unsigned long n_f_dpy = 0;
static unsigned long n_f_query = 0;
static unsigned long n_f_geo = 0;
static unsigned long n_moved = 0;
// Event feed histogram (SDL_PollEvent interpose, observe-only).
static unsigned long n_poll = 0;
static unsigned long n_ev_motion = 0;
static unsigned long n_ev_btn = 0;
static unsigned long n_ev_win = 0;
static long long last_ev_ms = 0;
// Event-coordinate stats (SDL3 SDL_MouseMotionEvent layout: type@0 u32,
// windowID@16 u32, x@28 f32, y@32 f32; button events share the prefix).
// Self-validating: windowID must match a tracked SDL window id, coords must
// be finite and sane, otherwise counted insane and ignored (wrong offsets).
static float ev_mx0 = 0;
static float ev_mx1 = 0;
static float ev_my0 = 0;
static float ev_my1 = 0;
static float ev_mlx = 0;
static float ev_mly = 0;
static float ev_blx = 0;
static float ev_bly = 0;
static unsigned ev_last_wid = 0;
static unsigned ev_pwid = 0;
static float ev_px = 0;
static float ev_py = 0;
static int ev_pok = 0;
static int ev_have = 0;
static unsigned long n_ev_insane = 0;
static unsigned ghost_wid = 0;
static float pair_vx = 0;
static float pair_vy = 0;
static int have_pair = 0;
static unsigned long long last_tracked_ts = 0;
static float last_tracked_x = 0;
static float last_tracked_y = 0;
static unsigned last_tracked_wid = 0;
static unsigned long long last_ghost_ts = 0;
static float last_ghost_x = 0;
static float last_ghost_y = 0;
static unsigned last_ghost_wid = 0;
static unsigned long n_pair = 0;
static unsigned long n_remap_ev = 0;
static unsigned long n_remap_state = 0;
static unsigned focus_last_id = 0xFFFFFFFFu;
static SDL_Window *(*real_GetMouseFocus)( void ) = 0;
static unsigned (*real_GetMouseState)( float *, float * ) = 0;
static unsigned long n_ev_nowid = 0;
static int evbad_left = 8;
static unsigned nowid_list[4] = { 0, 0, 0, 0 };
static int nowid_done[4] = { 0, 0, 0, 0 };
static SDL_Window *(*real_GetWindowFromID)( unsigned ) = 0;
static int fromid_logged = 0;

static int log_fd = -1;
static int log_failed = 0;

// Persistent X connection. One Display shared by all our queries on all
// threads, guarded by our own mutex (SDL uses its own connection, so there
// is no lock-domain interaction). Reconnected lazily; reconnect attempts
// throttled to 1 Hz so a dead X never becomes a syscall storm.
static Display *g_dpy = 0;
static pthread_mutex_t g_dpy_mutex = PTHREAD_MUTEX_INITIALIZER;
static long long g_dpy_fail_ms = 0;

// SBOX_SDLWINFIX unset/0/empty = dormant passthrough, no logging.
// "observe" = log only. Anything else ("1", "on", ...) = correct.
static int mode_cached = -1;
static int mode( void )
{
	if ( mode_cached >= 0 )
	{
		return mode_cached;
	}
	const char *v = getenv( "SBOX_SDLWINFIX" );
	int m = 0;
	if ( v && v[0] )
	{
		if ( strcmp( v, "0" ) != 0 && strcmp( v, "off" ) != 0 && strcmp( v, "false" ) != 0 )
		{
			m = ( strcmp( v, "observe" ) == 0 || strcmp( v, "log" ) == 0 ) ? 1 : 2;
		}
	}
	mode_cached = m;
	return m;
}

static long long now_ms( void )
{
	struct timespec ts;
	clock_gettime( CLOCK_REALTIME, &ts );
	return (long long)ts.tv_sec * 1000LL + ts.tv_nsec / 1000000LL;
}

static void log_open( void )
{
	if ( log_fd >= 0 || log_failed )
	{
		return;
	}
	const char *path = getenv( "SDLWINFIX_LOG" );
	if ( !path || !path[0] )
	{
		path = "logs/sdlwinfix.log";
	}
	log_fd = open( path, O_WRONLY | O_CREAT | O_APPEND, 0644 );
	if ( log_fd < 0 )
	{
		log_failed = 1;
	}
}

static void emit( const char *buf, int len )
{
	if ( mode() == 0 )
	{
		return;
	}
	log_open();
	if ( log_fd < 0 || len <= 0 )
	{
		return;
	}
	(void)!write( log_fd, buf, (size_t)len );
}

static void emitf( const char *fmt, ... )
{
	char buf[384];
	va_list ap;
	va_start( ap, fmt );
	int len = vsnprintf( buf, sizeof( buf ), fmt, ap );
	va_end( ap );
	if ( len <= 0 )
	{
		return;
	}
	if ( len >= (int)sizeof( buf ) )
	{
		len = (int)sizeof( buf ) - 1;
	}
	emit( buf, len );
}

static WindowEntry *track_window( SDL_Window *window )
{
	int free_slot = -1;
	for ( int i = 0; i < MAX_WINDOWS; i++ )
	{
		if ( windows[i].known && windows[i].ptr == window )
		{
			return &windows[i];
		}
		if ( !windows[i].known && free_slot < 0 )
		{
			free_slot = i;
		}
	}
	if ( free_slot < 0 )
	{
		return 0;
	}
	WindowEntry *e = &windows[free_slot];
	memset( e, 0, sizeof( *e ) );
	e->ptr = window;
	e->known = 1;
	if ( real_GetWindowID )
	{
		e->id = real_GetWindowID( window );
	}
	if ( real_GetWindowTitle )
	{
		const char *t = real_GetWindowTitle( window );
		if ( t )
		{
			strncpy( e->title, t, TITLE_LEN - 1 );
			e->title[TITLE_LEN - 1] = '\0';
		}
	}
	return e;
}

// Resolve the X geometry behind an SDL window over the persistent connection.
// Returns 1 with parent geometry when embedded (parent != root), 0 for a
// top-level window (pass through), or -reason on any failure (fail open).
static int x11_parent( SDL_Window *window, int *px, int *py, int *pw, int *ph,
	unsigned long *xid_out, unsigned long *parent_out, int *wx, int *wy, int *ww, int *wh )
{
	if ( !real_GetWindowProperties || !real_GetNumberProperty )
	{
		real_GetWindowProperties = dlsym( RTLD_NEXT, "SDL_GetWindowProperties" );
		real_GetNumberProperty = dlsym( RTLD_NEXT, "SDL_GetNumberProperty" );
	}
	if ( !real_GetWindowProperties || !real_GetNumberProperty )
	{
		n_f_sym++;
		return -FR_SYM;
	}
	SDL_PropertiesID props = real_GetWindowProperties( window );
	if ( !props )
	{
		n_f_props++;
		return -FR_PROPS;
	}
	unsigned long xid = (unsigned long)real_GetNumberProperty( props, "SDL.window.x11.window", 0 );
	if ( !xid )
	{
		n_f_xid++;
		return -FR_XID;
	}
	pthread_mutex_lock( &g_dpy_mutex );
	if ( !g_dpy )
	{
		long long now = now_ms();
		if ( now - g_dpy_fail_ms < 1000 )
		{
			pthread_mutex_unlock( &g_dpy_mutex );
			n_f_dpy++;
			return -FR_DPY;
		}
		g_dpy = XOpenDisplay( NULL );
		if ( !g_dpy )
		{
			g_dpy_fail_ms = now;
			pthread_mutex_unlock( &g_dpy_mutex );
			n_f_dpy++;
			return -FR_DPY;
		}
	}
	int ret = 0;
	Window root = 0;
	Window parent = 0;
	Window *children = 0;
	unsigned int nchildren = 0;
	if ( XQueryTree( g_dpy, (Window)xid, &root, &parent, &children, &nchildren ) )
	{
		if ( children )
		{
			XFree( children );
		}
		if ( parent != 0 && parent != root )
		{
			int x = 0;
			int y = 0;
			unsigned int w = 0;
			unsigned int h = 0;
			unsigned int bw = 0;
			unsigned int depth = 0;
			Window wroot = 0;
			if ( XGetGeometry( g_dpy, parent, &wroot, &x, &y, &w, &h, &bw, &depth ) )
			{
				*px = x;
				*py = y;
				*pw = (int)w;
				*ph = (int)h;
				ret = 1;
				// Own geometry for the topo line (best effort).
				if ( wx && XGetGeometry( g_dpy, (Window)xid, &wroot, &x, &y, &w, &h, &bw, &depth ) )
				{
					*wx = x;
					*wy = y;
					*ww = (int)w;
					*wh = (int)h;
				}
			}
			else
			{
				n_f_geo++;
				ret = -FR_GEO;
			}
		}
		else
		{
			n_top++;
			ret = 0;
		}
	}
	else
	{
		n_f_query++;
		ret = -FR_QUERY;
	}
	if ( ret < 0 )
	{
		// Drop the connection on X errors (stale xid, server hiccup) so the
		// next query reconnects instead of failing forever on a dead wire.
		XCloseDisplay( g_dpy );
		g_dpy = 0;
	}
	if ( xid_out )
	{
		*xid_out = xid;
	}
	if ( parent_out )
	{
		*parent_out = (unsigned long)parent;
	}
	pthread_mutex_unlock( &g_dpy_mutex );
	return ret;
}

static void log_topo_once( WindowEntry *e, unsigned long xid, unsigned long parent,
	int wx, int wy, int ww, int wh, int px, int py, int pw, int ph )
{
	if ( e->topo_done )
	{
		return;
	}
	e->topo_done = 1;
	emitf( "t=%lld topo id=%u xid=%lu parent=%lu wingeo=%d,%d,%dx%d pargeo=%d,%d,%dx%d\n",
		now_ms(), (unsigned)e->id, xid, parent, wx, wy, ww, wh, px, py, pw, ph );
}

static void log_ans( WindowEntry *e, const char *kind, int a, int b, int src )
{
	if ( e->lsrc == src )
	{
		if ( kind[0] == 's' && e->lw == a && e->lh == b )
		{
			return;
		}
		if ( kind[0] == 'p' && e->lx == a && e->ly == b )
		{
			return;
		}
	}
	if ( kind[0] == 's' )
	{
		e->lw = a;
		e->lh = b;
	}
	else
	{
		e->lx = a;
		e->ly = b;
	}
	e->lsrc = src;
	if ( kind[0] == 's' )
	{
		emitf( "t=%lld ans id=%u \"%s\" size %dx%d src=%s\n",
			now_ms(), (unsigned)e->id, e->title, a, b, src == 2 ? "parent" : "real" );
	}
	else
	{
		emitf( "t=%lld ans id=%u \"%s\" pos %d,%d src=%s\n",
			now_ms(), (unsigned)e->id, e->title, a, b, src == 2 ? "parent" : "real" );
	}
}

// Find a tracked window by SDL window id.
static WindowEntry *entry_for_id( unsigned wid )
{
	if ( wid == 0 )
	{
		return 0;
	}
	for ( int i = 0; i < MAX_WINDOWS; i++ )
	{
		if ( windows[i].known && windows[i].id == wid )
		{
			return &windows[i];
		}
	}
	return 0;
}

// Record mouse event coordinates with self-validation. SDL3 motion/button
// events share the prefix type@0 u32, windowID@16 u32, x@28 f32, y@32 f32.
// The windowID must match a tracked window and coords must be finite/sane,
// else counted insane and ignored (offsets would be wrong).
static WindowEntry *note_coords( void *ev, int is_button )
{
	unsigned wid = 0;
	float x = 0;
	float y = 0;
	memcpy( &wid, (char *)ev + 16, sizeof( wid ) );
	memcpy( &x, (char *)ev + 28, sizeof( x ) );
	memcpy( &y, (char *)ev + 32, sizeof( y ) );
	if ( x != x || y != y || x > 1e6f || x < -1e6f || y > 1e6f || y < -1e6f )
	{
		n_ev_insane++;
		if ( evbad_left > 0 )
		{
			evbad_left--;
			emitf( "t=%lld evbad type=%s wid=%u x=%f y=%f\n",
				now_ms(), is_button ? "btn" : "motion", wid, x, y );
		}
		return 0;
	}
	ev_pwid = wid;
	ev_px = x;
	ev_py = y;
	ev_pok = ( x == x && y == y && x <= 1e6f && x >= -1e6f && y <= 1e6f && y >= -1e6f );
	WindowEntry *found = entry_for_id( wid );
	if ( !found )
	{
		n_ev_nowid++;
		for ( int k = 0; k < 4; k++ )
		{
			if ( nowid_list[k] == wid )
			{
				break;
			}
			if ( nowid_list[k] == 0 )
			{
				nowid_list[k] = wid;
				break;
			}
		}
		if ( evbad_left > 0 )
		{
			evbad_left--;
			emitf( "t=%lld evbad type=%s wid=%u x=%.1f y=%.1f\n",
				now_ms(), is_button ? "btn" : "motion", wid, x, y );
		}
	}
	else
	{
		ev_last_wid = wid;
	}
	if ( is_button )
	{
		ev_blx = x;
		ev_bly = y;
	}
	else
	{
		ev_mlx = x;
		ev_mly = y;
		if ( !ev_have )
		{
			ev_mx0 = ev_mx1 = x;
			ev_my0 = ev_my1 = y;
			ev_have = 1;
		}
		else
		{
			if ( x < ev_mx0 )
			{
				ev_mx0 = x;
			}
			if ( x > ev_mx1 )
			{
				ev_mx1 = x;
			}
			if ( y < ev_my0 )
			{
				ev_my0 = y;
			}
			if ( y > ev_my1 )
			{
				ev_my1 = y;
			}
		}
	}
	return found;
}

static const char *reason_name( int r )
{
	switch ( r )
	{
	case FR_OK: return "ok";
	case FR_SYM: return "sym";
	case FR_PROPS: return "props";
	case FR_XID: return "xid";
	case FR_DPY: return "dpy";
	case FR_QUERY: return "query";
	case FR_GEO: return "geo";
	default: return "?";
	}
}

// Change-gated fail/recovery line: first failure, reason changes, recovery.
static void log_fail( WindowEntry *e, int reason )
{
	if ( e->lfail == reason )
	{
		return;
	}
	e->lfail = reason;
	emitf( "t=%lld fail id=%u \"%s\" reason=%s\n",
		now_ms(), (unsigned)e->id, e->title, reason_name( reason ) );
}

// Move detection: did Qt/WM move or resize the embed between queries?
// Answers F: do our faked answers follow instantly when geometry changes?
static void log_move( WindowEntry *e, int px, int py, int pw, int ph,
	int wx, int wy, int ww, int wh )
{
	if ( e->have_parent && e->ppx == px && e->ppy == py && e->ppw == pw && e->pph == ph )
	{
		return;
	}
	if ( e->have_parent )
	{
		n_moved++;
		emitf( "t=%lld move id=%u \"%s\" win %d,%d,%dx%d par %d,%d,%dx%d -> %d,%d,%dx%d\n",
			now_ms(), (unsigned)e->id, e->title,
			wx, wy, ww, wh, e->ppx, e->ppy, e->ppw, e->pph, px, py, pw, ph );
	}
	e->have_parent = 1;
	e->ppx = px;
	e->ppy = py;
	e->ppw = pw;
	e->pph = ph;
}

// Shared query path for size/position: resolve parent geometry in observe and
// correct modes (so observe also proves lookup reliability), fake answers in
// correct mode only.
static int query_parent( WindowEntry *e, int *px, int *py, int *pw, int *ph,
	unsigned long *xid, unsigned long *parent, int *wx, int *wy, int *ww, int *wh )
{
	int m = mode();
	if ( m == 0 )
	{
		return 0;
	}
	int r = x11_parent( e->ptr, px, py, pw, ph, xid, parent, wx, wy, ww, wh );
	if ( r == 1 )
	{
		log_topo_once( e, *xid, *parent, *wx, *wy, *ww, *wh, *px, *py, *pw, *ph );
		log_fail( e, FR_OK );
		log_move( e, *px, *py, *pw, *ph, *wx, *wy, *ww, *wh );
		return ( m == 2 ) ? 1 : 0;
	}
	if ( r < 0 )
	{
		log_fail( e, -r );
	}
	return 0;
}

// ---- interposed functions ----

SDL_Window *SDL_CreateWindowWithProperties( SDL_PropertiesID props )
{
	if ( !real_CreateWindowWithProperties )
	{
		real_CreateWindowWithProperties = dlsym( RTLD_NEXT, "SDL_CreateWindowWithProperties" );
		real_GetWindowID = dlsym( RTLD_NEXT, "SDL_GetWindowID" );
		real_GetWindowTitle = dlsym( RTLD_NEXT, "SDL_GetWindowTitle" );
	}
	n_create++;
	if ( !real_CreateWindowWithProperties )
	{
		return 0;
	}
	SDL_Window *w = real_CreateWindowWithProperties( props );
	if ( !w )
	{
		return w;
	}
	WindowEntry *e = track_window( w );
	if ( e )
	{
		emitf( "t=%lld create ptr=%p id=%u \"%s\"\n", now_ms(), (void *)w, (unsigned)e->id, e->title );
	}
	return w;
}

int SDL_GetWindowSize( SDL_Window *window, int *rw, int *rh )
{
	if ( !real_GetWindowSize )
	{
		real_GetWindowSize = dlsym( RTLD_NEXT, "SDL_GetWindowSize" );
	}
	n_getsize++;
	if ( !real_GetWindowSize )
	{
		return -1;
	}
	int rc = real_GetWindowSize( window, rw, rh );
	if ( !window || rc < 0 )
	{
		return rc;
	}
	WindowEntry *e = track_window( window );
	if ( !e )
	{
		return rc;
	}
	e->nq++;
	int w = rw ? *rw : -1;
	int h = rh ? *rh : -1;
	e->rw = w;
	e->rh = h;
	int src = 1;
	int px = 0;
	int py = 0;
	int pw = 0;
	int ph = 0;
	unsigned long xid = 0;
	unsigned long parent = 0;
	int wx = 0;
	int wy = 0;
	int ww = 0;
	int wh = 0;
	if ( query_parent( e, &px, &py, &pw, &ph, &xid, &parent, &wx, &wy, &ww, &wh ) )
	{
		if ( pw >= 1 && ph >= 1 )
		{
			w = pw;
			h = ph;
			if ( rw )
			{
				*rw = w;
			}
			if ( rh )
			{
				*rh = h;
			}
			src = 2;
			n_faked++;
		}
	}
	log_ans( e, "size", w, h, src );
	return rc;
}

int SDL_GetWindowPosition( SDL_Window *window, int *rx, int *ry )
{
	if ( !real_GetWindowPosition )
	{
		real_GetWindowPosition = dlsym( RTLD_NEXT, "SDL_GetWindowPosition" );
	}
	n_getposition++;
	if ( !real_GetWindowPosition )
	{
		return -1;
	}
	int rc = real_GetWindowPosition( window, rx, ry );
	if ( !window || rc < 0 )
	{
		return rc;
	}
	WindowEntry *e = track_window( window );
	if ( !e )
	{
		return rc;
	}
	e->nq++;
	int x = rx ? *rx : -999999;
	int y = ry ? *ry : -999999;
	int src = 1;
	int px = 0;
	int py = 0;
	int pw = 0;
	int ph = 0;
	unsigned long xid = 0;
	unsigned long parent = 0;
	int wx = 0;
	int wy = 0;
	int ww = 0;
	int wh = 0;
	if ( query_parent( e, &px, &py, &pw, &ph, &xid, &parent, &wx, &wy, &ww, &wh ) )
	{
		x = px;
		y = py;
		if ( rx )
		{
			*rx = x;
		}
		if ( ry )
		{
			*ry = y;
		}
		src = 2;
		n_faked++;
	}
	log_ans( e, "pos", x, y, src );
	return rc;
}

// Most-queried tracked window: the one the engine sizes/cursors from.
static WindowEntry *main_entry( void )
{
	WindowEntry *best = 0;
	for ( int i = 0; i < MAX_WINDOWS; i++ )
	{
		if ( windows[i].known && ( !best || windows[i].nq > best->nq ) )
		{
			best = &windows[i];
		}
	}
	return ( best && best->nq > 0 ) ? best : 0;
}

// Ghost-twin unification. A never-queried window echoing same-timestamp
// mouse events is a ghost twin living in a shifted space; pair it with the
// main window on identical timestamps, then rewrite its coords into main
// space (correct mode only - observe only logs the pair).
static void pair_or_remap( void *ev, unsigned long long tsms )
{
	if ( !ev_pok )
	{
		return;
	}
	WindowEntry *main = main_entry();
	if ( !main )
	{
		return;
	}
	WindowEntry *e = entry_for_id( ev_pwid );
	if ( e )
	{
		if ( e->id == main->id && last_ghost_wid != 0 && last_ghost_wid != main->id )
		{
			unsigned long long dt = tsms > last_ghost_ts ? tsms - last_ghost_ts : last_ghost_ts - tsms;
			if ( dt <= 8 )
			{
				float vx = last_ghost_x - ev_px;
				float vy = last_ghost_y - ev_py;
				if ( !have_pair || ghost_wid != last_ghost_wid ||
					vx - pair_vx > 0.5f || pair_vx - vx > 0.5f ||
					vy - pair_vy > 0.5f || pair_vy - vy > 0.5f )
				{
					ghost_wid = last_ghost_wid;
					pair_vx = vx;
					pair_vy = vy;
					have_pair = 1;
					n_pair++;
					emitf( "t=%lld pair ghost=%u main=%u vec=%.1f,%.1f\n",
						now_ms(), ghost_wid, main->id, vx, vy );
				}
			}
		}
		last_tracked_ts = tsms;
		last_tracked_x = ev_px;
		last_tracked_y = ev_py;
		last_tracked_wid = ev_pwid;
		return;
	}
	last_ghost_ts = tsms;
	last_ghost_x = ev_px;
	last_ghost_y = ev_py;
	last_ghost_wid = ev_pwid;
	if ( last_tracked_wid != main->id )
	{
		return;
	}
	unsigned long long dt = tsms > last_tracked_ts ? tsms - last_tracked_ts : last_tracked_ts - tsms;
	if ( dt > 8 )
	{
		return;
	}
	float vx = ev_px - last_tracked_x;
	float vy = ev_py - last_tracked_y;
	if ( !have_pair || ghost_wid != ev_pwid ||
		vx - pair_vx > 0.5f || pair_vx - vx > 0.5f ||
		vy - pair_vy > 0.5f || pair_vy - vy > 0.5f )
	{
		ghost_wid = ev_pwid;
		pair_vx = vx;
		pair_vy = vy;
		have_pair = 1;
		n_pair++;
		emitf( "t=%lld pair ghost=%u main=%u vec=%.1f,%.1f\n",
			now_ms(), ghost_wid, main->id, vx, vy );
	}
	if ( have_pair && ghost_wid == ev_pwid && mode() == 2 )
	{
		float nx = ev_px - pair_vx;
		float ny = ev_py - pair_vy;
		memcpy( (char *)ev + 28, &nx, 4 );
		memcpy( (char *)ev + 32, &ny, 4 );
		n_remap_ev++;
		ev_px = nx;
		ev_py = ny;
	}
}

// Observe-only event-feed histogram. Never modifies events; proves whether
// the engine's mouse feed is alive while size/pos answers are faked.
// SDL3 event ids used: mouse motion 0x400, button down/up 0x401/0x402,
// window events 0x2xx. Only the type field (offset 0) is read.
int SDL_PollEvent( void *ev )
{
	if ( !real_PollEvent )
	{
		real_PollEvent = dlsym( RTLD_NEXT, "SDL_PollEvent" );
	}
	if ( !real_PollEvent )
	{
		return 0;
	}
	int rc = real_PollEvent( ev );
	if ( mode() == 0 )
	{
		return rc;
	}
	n_poll++;
	if ( rc != 0 && ev )
	{
		unsigned type = *(volatile unsigned *)ev;
		unsigned long long ts = 0;
		memcpy( &ts, (char *)ev + 8, sizeof( ts ) );
		unsigned long long tsms = ts / 1000000ULL;
		if ( type == 0x400 )
		{
			n_ev_motion++;
			note_coords( ev, 0 );
			pair_or_remap( ev, tsms );
		}
		else if ( type == 0x401 || type == 0x402 )
		{
			n_ev_btn++;
			note_coords( ev, 1 );
			pair_or_remap( ev, tsms );
			if ( type == 0x401 && ev_pok )
			{
				emitf( "t=%lld click wid=%u x=%.1f y=%.1f\n", now_ms(), ev_pwid, ev_px, ev_py );
			}
		}
		else if ( ( type & 0xf00 ) == 0x200 )
		{
			n_ev_win++;
		}
		long long now = now_ms();
		if ( now - last_ev_ms >= 5000 )
		{
			last_ev_ms = now;
			emitf( "t=%lld ev motion=%lu btn=%lu win=%lu poll=%lu\n",
				now, n_ev_motion, n_ev_btn, n_ev_win, n_poll );
			if ( !real_GetWindowFromID && !fromid_logged )
			{
				real_GetWindowFromID = dlsym( RTLD_NEXT, "SDL_GetWindowFromID" );
				fromid_logged = 1;
				emitf( "t=%lld eshim fromid=%p\n", now, (void *)real_GetWindowFromID );
			}
			for ( int k = 0; k < 4 && real_GetWindowFromID; k++ )
			{
				if ( nowid_list[k] == 0 || nowid_done[k] )
				{
					continue;
				}
				nowid_done[k] = 1;
				SDL_Window *uw = real_GetWindowFromID( nowid_list[k] );
				if ( !uw || !real_GetWindowSize )
				{
					emitf( "t=%lld ewin id=%u ptr=%p noresolve\n", now, nowid_list[k], (void *)uw );
					continue;
				}
				int ufw = -1;
				int ufh = -1;
				real_GetWindowSize( uw, &ufw, &ufh );
				int upx = 0, upy = 0, upw = 0, uph = 0, uwx = 0, uwy = 0, uww = 0, uwh = 0;
				unsigned long uxid = 0, upar = 0;
				int ur = x11_parent( uw, &upx, &upy, &upw, &uph, &uxid, &upar, &uwx, &uwy, &uww, &uwh );
				emitf( "t=%lld ewin id=%u fossil=%dx%d xid=%lu parent=%lu wingeo=%d,%d,%dx%d pargeo=%d,%d,%dx%d %s\n",
					now, nowid_list[k], ufw, ufh, uxid, upar, uwx, uwy, uww, uwh,
					upx, upy, upw, uph, ur == 1 ? "embedded" : ( ur == 0 ? "toplevel" : "fail" ) );
			}
			WindowEntry *le = entry_for_id( ev_last_wid );
			emitf( "t=%lld evpos wid=%u fossil=%dx%d mx=[%.1f..%.1f] my=[%.1f..%.1f] mlast=(%.1f,%.1f) blast=(%.1f,%.1f) insane=%lu\n",
				now, ev_last_wid, le ? le->rw : -1, le ? le->rh : -1,
				ev_mx0, ev_mx1, ev_my0, ev_my1, ev_mlx, ev_mly, ev_blx, ev_bly, n_ev_insane );
		}
	}
	return rc;
}

// SDL_GetMouseState reports coords relative to the mouse-focus window.
// When focus sits on the ghost twin, translate into main space (correct
// mode only). Deltas are translation-invariant - relative mode untouched.
unsigned SDL_GetMouseState( float *x, float *y )
{
	if ( !real_GetMouseState )
	{
		real_GetMouseState = dlsym( RTLD_NEXT, "SDL_GetMouseState" );
	}
	if ( !real_GetMouseState )
	{
		return 0;
	}
	unsigned r = real_GetMouseState( x, y );
	if ( mode() != 2 || !have_pair || !x || !y )
	{
		return r;
	}
	if ( !real_GetMouseFocus )
	{
		real_GetMouseFocus = dlsym( RTLD_NEXT, "SDL_GetMouseFocus" );
	}
	if ( !real_GetMouseFocus || !real_GetWindowID )
	{
		return r;
	}
	SDL_Window *f = real_GetMouseFocus();
	unsigned fid = f ? real_GetWindowID( f ) : 0;
	if ( fid != focus_last_id )
	{
		focus_last_id = fid;
		emitf( "t=%lld focus id=%u\n", now_ms(), fid );
	}
	if ( fid == ghost_wid )
	{
		*x -= pair_vx;
		*y -= pair_vy;
		n_remap_state++;
	}
	return r;
}

__attribute__( ( destructor ) ) static void sdlwinfix_summary( void )
{
	if ( mode() == 0 )
	{
		return;
	}
	emitf( "t=%lld summary create=%lu getsize=%lu getposition=%lu faked=%lu top=%lu fail_sym=%lu fail_props=%lu fail_xid=%lu fail_dpy=%lu fail_query=%lu fail_geo=%lu moved=%lu motion=%lu btn=%lu winev=%lu poll=%lu insane=%lu mlast=(%.1f,%.1f) blast=(%.1f,%.1f)\n",
		now_ms(), n_create, n_getsize, n_getposition, n_faked, n_top,
		n_f_sym, n_f_props, n_f_xid, n_f_dpy, n_f_query, n_f_geo,
		n_moved, n_ev_motion, n_ev_btn, n_ev_win, n_poll,
		n_ev_insane, ev_mlx, ev_mly, ev_blx, ev_bly );
	emitf( "t=%lld summary3 ghost=%u vec=(%.1f,%.1f) pair=%lu remap_ev=%lu remap_state=%lu focus=%u\n",
		now_ms(), ghost_wid, pair_vx, pair_vy, n_pair, n_remap_ev, n_remap_state,
		focus_last_id == 0xFFFFFFFFu ? 0 : focus_last_id );
	emitf( "t=%lld summary2 nowid=%lu\n", now_ms(), n_ev_nowid );
	if ( log_fd >= 0 )
	{
		close( log_fd );
		log_fd = -1;
	}
	pthread_mutex_lock( &g_dpy_mutex );
	if ( g_dpy )
	{
		XCloseDisplay( g_dpy );
		g_dpy = 0;
	}
	pthread_mutex_unlock( &g_dpy_mutex );
}
