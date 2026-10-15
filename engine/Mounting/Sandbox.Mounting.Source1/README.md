# Source 1 mount

The engine's `gmod` mount reads supported models, materials, textures and sounds
from VPKs and loose files inside the Garry's Mod installation.

## Material dependency resolution

VMT parameters target Windows PC DX90/PS2.0b. Shader fallback blocks such as
`>=dx90_20b` and the `$DX90_20B` condition are supported. Both combine soldier
skins keep their base texture and self-illumination mask from those blocks.

There is no arbitrary basename matching or cross-installation dependency
fallback. Genuinely missing assets remain reported rather than receiving
unrelated replacement materials.
