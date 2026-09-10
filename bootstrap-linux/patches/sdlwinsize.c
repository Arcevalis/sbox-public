// sdlwinsize.c — observation-only SDL3 window-size spy for the s&box
// Linux/XWayland fullscreen-menu investigation (Phase B).
//
// Interposes ONLY window creation, size/position/scale queries and explicit
// resize calls. It never alters arguments or return values, never touches the
// event queue, mouse grab/mode, or the Vulkan surface path, and performs a
// single write() per log line. Safe under LD_PRELOAD ahead of the engine's
// libSDL3.so.0.4.14, alongside the HarfBuzz preload (libHarfBuzzSharp first).
//
//   LD_PRELOAD=/path/to/libsdlwinsize.so SDLWINSIZE_LOG=/tmp/sdlsize.log ampersand
//
// Log lines use CLOCK_REALTIME epoch ms so they correlate with managed
// [uisize] lines:
//   t=<ms> create ptr=<p> id=<id> "<title>" display=<d> content_scale=<c>
//   t=<ms> sz id=<id> "<title>" logical=<w>x<h> pixels=<pw>x<ph> density=<d> dscale=<s>
//   t=<ms> pos id=<id> "<title>" x=<x> y=<y>
//   t=<ms> setsize id=<id> <w>x<h>
//   t=<ms> setfullscreen id=<id> <0|1>
// Size/scale/position lines are emitted only when the value changed for that
// window. A destructor flushes per-function call counters (proves the poll rate
// without log spam). Truncate the log per run; the shim only appends.
//
// Build with: gcc -D_GNU_SOURCE -shared -fPIC -O1 -o libsdlwinsize.so sdlwinsize.c -ldl

#include <dlfcn.h>
#include <fcntl.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

typedef struct SDL_Window SDL_Window;
typedef uint32_t SDL_PropertiesID;
typedef uint32_t SDL_WindowID;
typedef uint32_t SDL_DisplayID;

// ---- real function pointers (resolved lazily via RTLD_NEXT) ----
static SDL_Window *( *real_CreateWindowWithProperties )( SDL_PropertiesID props ) = 0;
static int ( *real_GetWindowSize )( SDL_Window *window, int *w, int *h ) = 0;
static int ( *real_GetWindowPosition )( SDL_Window *window, int *x, int *y ) = 0;
static int ( *real_GetWindowSizeInPixels )( SDL_Window *window, int *w, int *h ) = 0;
static float ( *real_GetWindowPixelDensity )( SDL_Window *window ) = 0;
static float ( *real_GetWindowDisplayScale )( SDL_Window *window ) = 0;
static void ( *real_SetWindowSize )( SDL_Window *window, int w, int h ) = 0;
static int ( *real_SetWindowFullscreen )( SDL_Window *window, bool fullscreen ) = 0;

// Non-interposed getters used for window identity (never hooked, no recursion).
static SDL_WindowID ( *real_GetWindowID )( SDL_Window *window ) = 0;
static const char *( *real_GetWindowTitle )( SDL_Window *window ) = 0;
static SDL_DisplayID ( *real_GetDisplayForWindow )( SDL_Window *window ) = 0;
static float ( *real_GetDisplayContentScale )( SDL_DisplayID display ) = 0;

// ---- counters (flushed by the destructor) ----
static unsigned long n_create = 0;
static unsigned long n_getsize = 0;
static unsigned long n_getposition = 0;
static unsigned long n_getsizepixels = 0;
static unsigned long n_density = 0;
static unsigned long n_displayscale = 0;
static unsigned long n_setsize = 0;
static unsigned long n_setfullscreen = 0;

// ---- per-window cache (32 entries, fixed, no allocation) ----
#define MAX_WINDOWS 32
#define TITLE_LEN 64
typedef struct
{
	SDL_Window *ptr;
	SDL_WindowID id;
	char title[TITLE_LEN];
	int w;
	int h;
	int px;
	int py;
	int pw;
	int ph;
	float density;
	float dscale;
	int known;
} WindowEntry;

static WindowEntry windows[MAX_WINDOWS];

// ---- log fd (lazy) ----
static int log_fd = -1;
static int log_failed = 0;

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
	const char *path = getenv( "SDLWINSIZE_LOG" );
	if ( !path || !path[0] )
	{
		// Relative so it lands in game/logs/ when launched from game/
		// (launch scripts cd there); game/logs is git-ignored.
		path = "logs/sdlsize.log";
	}
	log_fd = open( path, O_WRONLY | O_CREAT | O_APPEND, 0644 );
	if ( log_fd < 0 )
	{
		log_failed = 1;
	}
}

static void emit( const char *buf, int len )
{
	log_open();
	if ( log_fd < 0 )
	{
		return;
	}
	if ( len <= 0 )
	{
		return;
	}
	// Best effort single write; never retry, never block the engine.
	(void)!write( log_fd, buf, (size_t)len );
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
	e->w = e->h = e->pw = e->ph = -1;
	e->px = e->py = -999999;
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

// Refresh identity fields opportunistically (title can change after creation).
static void refresh_identity( WindowEntry *e )
{
	if ( real_GetWindowID )
	{
		SDL_WindowID id = real_GetWindowID( e->ptr );
		if ( id )
		{
			e->id = id;
		}
	}
	if ( real_GetWindowTitle )
	{
		const char *t = real_GetWindowTitle( e->ptr );
		if ( t && strncmp( e->title, t, TITLE_LEN ) != 0 )
		{
			strncpy( e->title, t, TITLE_LEN - 1 );
			e->title[TITLE_LEN - 1] = '\0';
		}
	}
}

// ---- interposed functions ----

SDL_Window *SDL_CreateWindowWithProperties( SDL_PropertiesID props )
{
	if ( !real_CreateWindowWithProperties )
	{
		real_CreateWindowWithProperties = dlsym( RTLD_NEXT, "SDL_CreateWindowWithProperties" );
		real_GetWindowID = dlsym( RTLD_NEXT, "SDL_GetWindowID" );
		real_GetWindowTitle = dlsym( RTLD_NEXT, "SDL_GetWindowTitle" );
		real_GetDisplayForWindow = dlsym( RTLD_NEXT, "SDL_GetDisplayForWindow" );
		real_GetDisplayContentScale = dlsym( RTLD_NEXT, "SDL_GetDisplayContentScale" );
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
	SDL_WindowID id = real_GetWindowID ? real_GetWindowID( w ) : 0;
	const char *title = real_GetWindowTitle ? real_GetWindowTitle( w ) : 0;
	SDL_DisplayID display = real_GetDisplayForWindow ? real_GetDisplayForWindow( w ) : 0;
	float content_scale = real_GetDisplayContentScale && display ? real_GetDisplayContentScale( display ) : 0.0f;
	WindowEntry *e = track_window( w );
	if ( e )
	{
		e->id = id;
	}
	char buf[384];
	int len = snprintf( buf, sizeof( buf ), "t=%lld create ptr=%p id=%u \"%s\" display=%u content_scale=%.2f\n",
		now_ms(), (void *)w, (unsigned)id, title ? title : "", (unsigned)display, content_scale );
	if ( len > 0 )
	{
		if ( len >= (int)sizeof( buf ) )
		{
			len = (int)sizeof( buf ) - 1;
		}
		emit( buf, len );
	}
	return w;
}

int SDL_GetWindowSize( SDL_Window *window, int *rw, int *rh )
{
	if ( !real_GetWindowSize )
	{
		real_GetWindowSize = dlsym( RTLD_NEXT, "SDL_GetWindowSize" );
		real_GetWindowID = dlsym( RTLD_NEXT, "SDL_GetWindowID" );
		real_GetWindowTitle = dlsym( RTLD_NEXT, "SDL_GetWindowTitle" );
	}
	n_getsize++;
	if ( !real_GetWindowSize )
	{
		return -1;
	}
	int rc = real_GetWindowSize( window, rw, rh );
	if ( !window )
	{
		char buf[128];
		int len = snprintf( buf, sizeof( buf ), "t=%lld sz? null-window rc=%d\n", now_ms(), rc );
		if ( len > 0 )
		{
			emit( buf, len > (int)sizeof( buf ) - 1 ? (int)sizeof( buf ) - 1 : len );
		}
		return rc;
	}
	int w = rw ? *rw : -1;
	int h = rh ? *rh : -1;
	WindowEntry *e = track_window( window );
	if ( !e )
	{
		return rc;
	}
	if ( rc < 0 )
	{
		char buf[192];
		int len = snprintf( buf, sizeof( buf ), "t=%lld sz? id=%u ptr=%p rc=%d\n",
			now_ms(), (unsigned)e->id, (void *)window, rc );
		if ( len > 0 )
		{
			emit( buf, len > (int)sizeof( buf ) - 1 ? (int)sizeof( buf ) - 1 : len );
		}
		return rc;
	}
	if ( e->w == w && e->h == h )
	{
		return rc;
	}
	e->w = w;
	e->h = h;
	refresh_identity( e );
	// Re-read scale companions opportunistically so each sz line is complete.
	if ( real_GetWindowSizeInPixels )
	{
		int pw = -1;
		int ph = -1;
		if ( real_GetWindowSizeInPixels( window, &pw, &ph ) == 0 )
		{
			e->pw = pw;
			e->ph = ph;
		}
	}
	if ( real_GetWindowPixelDensity )
	{
		e->density = real_GetWindowPixelDensity( window );
	}
	if ( real_GetWindowDisplayScale )
	{
		e->dscale = real_GetWindowDisplayScale( window );
	}
	char buf[384];
	int len = snprintf( buf, sizeof( buf ), "t=%lld sz id=%u \"%s\" logical=%dx%d pixels=%dx%d density=%.2f dscale=%.2f\n",
		now_ms(), (unsigned)e->id, e->title, e->w, e->h, e->pw, e->ph, e->density, e->dscale );
	if ( len > 0 )
	{
		if ( len >= (int)sizeof( buf ) )
		{
			len = (int)sizeof( buf ) - 1;
		}
		emit( buf, len );
	}
	return rc;
}

int SDL_GetWindowPosition( SDL_Window *window, int *rx, int *ry )
{
	if ( !real_GetWindowPosition )
	{
		real_GetWindowPosition = dlsym( RTLD_NEXT, "SDL_GetWindowPosition" );
		real_GetWindowID = dlsym( RTLD_NEXT, "SDL_GetWindowID" );
		real_GetWindowTitle = dlsym( RTLD_NEXT, "SDL_GetWindowTitle" );
	}
	n_getposition++;
	if ( !real_GetWindowPosition )
	{
		return -1;
	}
	int rc = real_GetWindowPosition( window, rx, ry );
	if ( !window )
	{
		char buf[128];
		int len = snprintf( buf, sizeof( buf ), "t=%lld pos? null-window rc=%d\n", now_ms(), rc );
		if ( len > 0 )
		{
			emit( buf, len > (int)sizeof( buf ) - 1 ? (int)sizeof( buf ) - 1 : len );
		}
		return rc;
	}
	int x = rx ? *rx : -999999;
	int y = ry ? *ry : -999999;
	WindowEntry *e = track_window( window );
	if ( !e )
	{
		return rc;
	}
	if ( rc < 0 )
	{
		char buf[192];
		int len = snprintf( buf, sizeof( buf ), "t=%lld pos? id=%u ptr=%p rc=%d\n",
			now_ms(), (unsigned)e->id, (void *)window, rc );
		if ( len > 0 )
		{
			emit( buf, len > (int)sizeof( buf ) - 1 ? (int)sizeof( buf ) - 1 : len );
		}
		return rc;
	}
	if ( e->px == x && e->py == y )
	{
		return rc;
	}
	e->px = x;
	e->py = y;
	refresh_identity( e );
	char buf[384];
	int len = snprintf( buf, sizeof( buf ), "t=%lld pos id=%u \"%s\" x=%d y=%d\n",
		now_ms(), (unsigned)e->id, e->title, e->px, e->py );
	if ( len > 0 )
	{
		if ( len >= (int)sizeof( buf ) )
		{
			len = (int)sizeof( buf ) - 1;
		}
		emit( buf, len );
	}
	return rc;
}

int SDL_GetWindowSizeInPixels( SDL_Window *window, int *rw, int *rh )
{
	if ( !real_GetWindowSizeInPixels )
	{
		real_GetWindowSizeInPixels = dlsym( RTLD_NEXT, "SDL_GetWindowSizeInPixels" );
		real_GetWindowID = dlsym( RTLD_NEXT, "SDL_GetWindowID" );
		real_GetWindowTitle = dlsym( RTLD_NEXT, "SDL_GetWindowTitle" );
	}
	n_getsizepixels++;
	if ( !real_GetWindowSizeInPixels )
	{
		return -1;
	}
	int rc = real_GetWindowSizeInPixels( window, rw, rh );
	if ( !window || rc < 0 )
	{
		return rc;
	}
	int w = rw ? *rw : -1;
	int h = rh ? *rh : -1;
	WindowEntry *e = track_window( window );
	if ( !e )
	{
		return rc;
	}
	if ( e->pw == w && e->ph == h )
	{
		return rc;
	}
	e->pw = w;
	e->ph = h;
	refresh_identity( e );
	char buf[384];
	int len = snprintf( buf, sizeof( buf ), "t=%lld sz id=%u \"%s\" logical=%dx%d pixels=%dx%d density=%.2f dscale=%.2f\n",
		now_ms(), (unsigned)e->id, e->title, e->w, e->h, e->pw, e->ph, e->density, e->dscale );
	if ( len > 0 )
	{
		if ( len >= (int)sizeof( buf ) )
		{
			len = (int)sizeof( buf ) - 1;
		}
		emit( buf, len );
	}
	return rc;
}

float SDL_GetWindowPixelDensity( SDL_Window *window )
{
	if ( !real_GetWindowPixelDensity )
	{
		real_GetWindowPixelDensity = dlsym( RTLD_NEXT, "SDL_GetWindowPixelDensity" );
		real_GetWindowID = dlsym( RTLD_NEXT, "SDL_GetWindowID" );
		real_GetWindowTitle = dlsym( RTLD_NEXT, "SDL_GetWindowTitle" );
	}
	n_density++;
	if ( !real_GetWindowPixelDensity )
	{
		return 0.0f;
	}
	float d = real_GetWindowPixelDensity( window );
	if ( !window )
	{
		return d;
	}
	WindowEntry *e = track_window( window );
	if ( !e || e->density == d )
	{
		return d;
	}
	e->density = d;
	refresh_identity( e );
	char buf[384];
	int len = snprintf( buf, sizeof( buf ), "t=%lld sz id=%u \"%s\" logical=%dx%d pixels=%dx%d density=%.2f dscale=%.2f\n",
		now_ms(), (unsigned)e->id, e->title, e->w, e->h, e->pw, e->ph, e->density, e->dscale );
	if ( len > 0 )
	{
		if ( len >= (int)sizeof( buf ) )
		{
			len = (int)sizeof( buf ) - 1;
		}
		emit( buf, len );
	}
	return d;
}

float SDL_GetWindowDisplayScale( SDL_Window *window )
{
	if ( !real_GetWindowDisplayScale )
	{
		real_GetWindowDisplayScale = dlsym( RTLD_NEXT, "SDL_GetWindowDisplayScale" );
		real_GetWindowID = dlsym( RTLD_NEXT, "SDL_GetWindowID" );
		real_GetWindowTitle = dlsym( RTLD_NEXT, "SDL_GetWindowTitle" );
	}
	n_displayscale++;
	if ( !real_GetWindowDisplayScale )
	{
		return 0.0f;
	}
	float s = real_GetWindowDisplayScale( window );
	if ( !window )
	{
		return s;
	}
	WindowEntry *e = track_window( window );
	if ( !e || e->dscale == s )
	{
		return s;
	}
	e->dscale = s;
	refresh_identity( e );
	char buf[384];
	int len = snprintf( buf, sizeof( buf ), "t=%lld sz id=%u \"%s\" logical=%dx%d pixels=%dx%d density=%.2f dscale=%.2f\n",
		now_ms(), (unsigned)e->id, e->title, e->w, e->h, e->pw, e->ph, e->density, e->dscale );
	if ( len > 0 )
	{
		if ( len >= (int)sizeof( buf ) )
		{
			len = (int)sizeof( buf ) - 1;
		}
		emit( buf, len );
	}
	return s;
}

void SDL_SetWindowSize( SDL_Window *window, int w, int h )
{
	if ( !real_SetWindowSize )
	{
		real_SetWindowSize = dlsym( RTLD_NEXT, "SDL_SetWindowSize" );
		real_GetWindowID = dlsym( RTLD_NEXT, "SDL_GetWindowID" );
	}
	n_setsize++;
	if ( !real_SetWindowSize )
	{
		return;
	}
	real_SetWindowSize( window, w, h );
	WindowEntry *e = window ? track_window( window ) : 0;
	char buf[192];
	int len = snprintf( buf, sizeof( buf ), "t=%lld setsize id=%u %dx%d\n",
		now_ms(), e ? (unsigned)e->id : 0, w, h );
	if ( len > 0 )
	{
		emit( buf, len > (int)sizeof( buf ) - 1 ? (int)sizeof( buf ) - 1 : len );
	}
}

int SDL_SetWindowFullscreen( SDL_Window *window, bool fullscreen )
{
	if ( !real_SetWindowFullscreen )
	{
		real_SetWindowFullscreen = dlsym( RTLD_NEXT, "SDL_SetWindowFullscreen" );
		real_GetWindowID = dlsym( RTLD_NEXT, "SDL_GetWindowID" );
	}
	n_setfullscreen++;
	if ( !real_SetWindowFullscreen )
	{
		return -1;
	}
	int rc = real_SetWindowFullscreen( window, fullscreen );
	WindowEntry *e = window ? track_window( window ) : 0;
	char buf[192];
	int len = snprintf( buf, sizeof( buf ), "t=%lld setfullscreen id=%u %d rc=%d\n",
		now_ms(), e ? (unsigned)e->id : 0, fullscreen ? 1 : 0, rc );
	if ( len > 0 )
	{
		emit( buf, len > (int)sizeof( buf ) - 1 ? (int)sizeof( buf ) - 1 : len );
	}
	return rc;
}

__attribute__( ( destructor ) ) static void sdlwinsize_summary( void )
{
	char buf[256];
		int len = snprintf( buf, sizeof( buf ),
		"t=%lld summary create=%lu getsize=%lu getposition=%lu getsizepixels=%lu density=%lu displayscale=%lu setsize=%lu setfullscreen=%lu\n",
		now_ms(), n_create, n_getsize, n_getposition, n_getsizepixels, n_density, n_displayscale, n_setsize, n_setfullscreen );
	if ( len > 0 )
	{
		if ( len >= (int)sizeof( buf ) )
		{
			len = (int)sizeof( buf ) - 1;
		}
		emit( buf, len );
	}
	if ( log_fd >= 0 )
	{
		close( log_fd );
		log_fd = -1;
	}
}
