# BrazeFX for Unity

GPU fire, smoke, particles, generated volumes, and baked fluid-cache playback for URP. A
`BrazeContainer` owns a sparse fluid simulation; child emitters, forces, and obstacles feed it.

BrazeFX 1.0 supports Unity 6 URP on Windows and macOS desktop, in both the Editor and desktop
Players. The validated graphics APIs are Direct3D 11 on Windows and Metal on macOS. Built-in RP,
HDRP, Linux, Android, iOS/iPadOS, Web/WebGPU, and consoles are not supported in this release.

Resources: [BrazeFX website](https://brazefx.com/) ·
[online documentation](https://brazefx.com/docs) ·
[community](https://brazefx.com/community)

## Scene setup

1. Run **Tools → BrazeFX → Setup → Configure URP Renderers…** once after importing. Opening a BrazeFX
   demo also offers this setup automatically. It adds the single **Braze Render Feature** to every project-owned
   renderer referenced by a URP Pipeline Asset, while preserving existing features and settings.
2. Use **GameObject → BrazeFX → Fire → Container** and size its Grid volume. The menu creates and
   selects a ready-to-use **Braze Container** GameObject; do not create an empty object first.
3. With the Container selected, use **GameObject → BrazeFX → Fire → Pyro Emitter**. The new emitter
   is parented to the Container. Choose a primitive or Mesh source and author smoke/fire.
4. Optionally add child components from **BrazeFX ▸ Fire ▸ Forces**, plus **Braze Obstacle** components.
5. Enter Play Mode.

If a camera explicitly overrides its renderer, that renderer must also contain the feature. Run the setup command
again after adding a new URP Pipeline/Renderer asset; it is safe to repeat and never creates duplicates.

After import, BrazeFX prepares its compute kernels once for the Editor's active graphics API. The visible
**BrazeFX — Preparing Compute Kernels** progress window is expected and keeps cold shader compilation out of the
first fire, smoke, or CFD Play Mode transition. Clearing the project's shader cache or changing graphics API repeats
this one-time preparation. Run **Tools → BrazeFX → Setup → Prepare Compute Kernels** to prepare them manually.

## Renderer quality presets

New **Braze Render Feature** instances use the **Mobile** preset: half-resolution live/cache and generated volumes,
reconstruction, and reduced march budgets. This performance-first starting point avoids an unexpectedly heavy first
run; select **PC** explicitly when native-resolution desktop authoring or capture is required.

| Preset | Rendering policy |
|--------|------------------|
| **PC** | Quality-first native resolution, full march budget, no temporal interleaving. |
| **Console** | Native-resolution output with reduced sampling and stronger importance stepping. |
| **Mobile** | Default performance tier. Half-resolution reconstruction and reduced budgets for constrained desktop projects and previews. |
| **Custom** | Preserves individual advanced settings. Editing a preset-owned field switches to Custom. |

**Mobile** and **Console** are performance-tier names; they do not claim Android, iOS, or console
runtime support.

Slice temporal interleaving remains available under **Advanced / Experimental**, but it is not enabled by any
shipping preset because some Unity/URP versions can expose its rotating pixel pattern as checkerboarding. Use
**Temporal Interleaving: Off** for release-quality output.

You can add **multiple** emitters and forces — the container gathers all children each frame.

## Object model

| Component | Role |
|-----------|------|
| `BrazeContainer` | Simulation domain + volume render. Owns the solver; gathers child emitters/forces. |
| `BrazePyroEmitter` | Primitive, mesh-surface, skinned-mesh, or Voronoi-fragment source. |
| Dedicated pyro forces | Wind, vortex, spline vortex, turbulence, gravity, attract/repel, disc, hold, billows, lift, jet, and spinner authoring components. They share the `BrazePyroForce` runtime engine. |
| `BrazeObstacle` | Solid collision boundary for the fluid. |
| `BrazeCacheBaker` / `BrazeCachePlayer` | Sparse cache recording and standalone resident/streamed playback. |
| `BrazeParticleSystem` | GPU particles rendered through the unified feature. |
| `BrazeVolume` | Procedural static volume with baked lighting and its dedicated renderer. |

## Runtime architecture

The solver uses 4³ occupancy bricks to dispatch emission, transport, forces, sparse RBGS pressure,
decay, Render Detail, and housekeeping only around active content. The fluid state still lives in
dense 3D textures, so grid memory scales approximately with resolution cubed even when compute is
sparse.

`BrazeRenderFeature` owns the live/cache slice renderer, generated-volume composite, volume shadows,
and particle passes. Render Detail is a visual-only GPU warp volume; it bends the existing field and
does not create simulation density. Mesh and Voronoi-fragment emission also stays on the GPU.

## Batched fracture impacts

Custom transform-driven projectiles can use `BrazeFractureImpactSource`; it sweeps their previous/current positions
without Unity Rigidbody, Collider, or raycasts. Hitscan weapons should queue impacts directly. Every command received
before the fracture's `LateUpdate` uploads once and resolves in one GPU dispatch:

```csharp
var impact = BrazeFractureImpact.Penetrating(hit.point, shotDirection, 0.2f, 0.5f, 14f);
impact.sourceVelocity = projectileVelocity;
impact.velocityInheritance = 0.5f;
fracture.QueueImpact(impact);
```

The Voronoi fracture owns this path under **Destruction > Impact Triggers (GPU)**; its Manual/Field choice controls
persistent authoring behaviour and does not disable transient impacts. Optional **Structural Support** uses the baked
shared-face graph to release islands that lose their route to the anchored boundary. An impact changes matching cells from
anchored to free and assigns their initial velocity/spin. Add a
`BrazeGravity` to the fracture's Forces list to make released cells fall, and Braze particle colliders to make them
contact the ground. Already-free cells can receive later impulses without any CPU readback or per-cell GameObjects.

Structural Support can add a temporary **Cohesive Collapse** layer. Cement, Wood, Metal, Plastic, and Glass presets
partition neighboring cells into deterministic macro-chunks, seed coherent toppling motion, preserve their rest-distance
and rotation relationships on the GPU, then break bonds by strain or material duration. Custom exposes chunk size,
cohesion, motion matching, break strain, bond duration, toppling, immediate rubble, and solver iterations. Constraint
dispatches exist only while a collapse window is active; fragments then return to the normal granular simulation.
When any non-root member loses connectivity, its whole continuous macro-group is activated together; unyielding root
cells remain anchored and cells detached directly by an impact stay granular, producing a detailed contact crater around
larger connected slabs.

## Quality modes and grid resolution

Changing Grid Resolution now preserves world-space motion relative to **Physics Reference Res**. Braze scales
voxel-space forces/frequencies and schedules resolution-aware advection substeps, so a finer grid adds spatial
detail instead of changing the effect's speed or scale.

| Mode | Intended use | Main limits |
|------|--------------|-------------|
| **Mobile** | Constrained desktop projects and fast previews | 64 grid, 30 Hz solver, 2-substep ceiling, 96 render steps, quarter-resolution march, 256 MB/container |
| **Realtime** | Interactive desktop | 128 grid, 60 Hz solver, 4-substep ceiling, 256 render steps, half-resolution march, 2 GB/container |
| **Production** | Offline bake/capture and high-memory GPUs | 256 starting grid (supports manual 512), 120 Hz solver, strict CFL, sparse RBGS pressure, 512 full-resolution render steps, 16 GB/container |
| **Custom** | Explicit project budgets | Exposes substep, CFL, render, and memory controls |

The Container inspector shows estimated allocation and rejects a grid before creating resources when it exceeds
the selected mode or the device's texture/buffer limits. The Stats HUD reports scheduled/required substeps; a
`LIMITED` marker means the chosen realtime budget is intentionally trading stability for cost. A dense 512³ live
simulation is still a production-scale allocation—selecting Production permits it, but does not make it inexpensive.

Existing serialized containers migrate to **Custom** and capture their current resolution as the reference, keeping
their prior realtime/render budgets unchanged.

Selecting Mobile, Realtime, or Production in the inspector applies its visible grid, pressure, solver-rate, Render
Detail, and shadow defaults immediately from the dropdown selection event. Those fields remain editable afterward;
selecting another preset applies that preset's values.
Physics Reference Res is never overwritten by a preset, so switching device quality does not retune world-space motion.

## Export Studio

Open **Window → BrazeFX → Export Studio** and bind a Container. The Studio automatically restores its project-local
settings from `Library`; no user-managed session asset is required. Set the complete simulation Length on the bottom
timeline. Its persistent blue Region overlay owns user-facing In/Out plus Once, Loop, Ping-Pong, or Crossfade Loop
playback. Drag either edge to trim it or drag its body to move it; transport and exporters obey the Region and snap to
the Container's simulation rate.

**Bake Cache** in the Master Cache panel saves dirty scenes when approved, enters Play Mode, records the complete simulation timeline,
returns to Edit Mode, creates or updates the cache asset, and assigns it back to
the Studio. A Cache Baker component is not required. New Studio sessions use lossless **Compact** storage and put
the large working binary in `BrazeFX Working Caches` outside `Assets`; only its small cache asset lives in the Unity
project. **Fast** remains available for a deliberately retained raw cache at a much larger disk cost. **Realtime** is
the bandwidth-first option: it keeps native Render Detail but stores scalar/vector values with local per-brick 8-bit
scales, roughly halving resident upload bytes at an explicit visual-quality tradeoff. Compare low-density wisps and
fire gradients before shipping it. The same Studio can also be opened from the Container, Timeline, or legacy Cache
Baker inspector.

Master **Purpose** defaults to **Full Playback And Flipbook**, preserving native-resolution Render Detail and the exact
Unity look. Choose **VDB Delivery** for a VDB-only job: that lean working source retains the render-conditioned scalar
fields and optional velocity consumed by OpenVDB, but omits renderer-only detail bricks. Flipbook export is disabled
for a lean cache when the source authored Render Detail, preventing an accidental lower-detail delivery. Realtime
storage cannot source authoritative OpenVDB: choosing VDB Delivery restores lossless Compact automatically, and the
VDB export control fails closed while a Realtime master is selected.

After a successful bake, the panel shows the exact file size and location and the Bake button becomes green. Scrub the
cache directly in Edit Mode; Export Studio automatically keeps the complete cache on the GPU when it safely fits the
detected device, otherwise it uses bounded streaming. Streaming prioritizes the playhead and neighbouring frames in
the transport direction, overlaps a small memory-budgeted set of decoders with byte-capped uploads, and buffers the
playback clock when a frame is late instead of racing ahead and becoming permanently stale. Random scrubbing remains
exact-frame-only. A looping Region supports Scene-view endpoint inspection, numerical seam scoring, suggested
low-difference endpoints, and non-destructive ping-pong or volume-crossfade derived caches. Cache frames render
directly in Unity's Scene view while the playhead is scrubbed; Export Studio does not duplicate the renderer with an
embedded preview. The renderer-ready cache is the deterministic working source for scrubbing, retries, flipbooks and
VDB conversion; it is not required after verified deliveries. For a VDB-only job, use **Clear Bake** after checking the
published sequence. The confirmation lists and validates every generated cache target before deleting anything.

Flipbook delivery supports front/back/left/right/top/bottom/isometric views, perspective or orthographic framing,
automatic or manual bounds, pivot, padding, atlas limits, PNG/TGA/EXR, premultiplied alpha, and alpha dilation. It
publishes one atlas per view, a JSON manifest, and a `BrazeFlipbookAsset`; **Create / Update Player** configures a
`BrazeFlipbookPlayer` in one click.

Volume delivery writes one dependency-free managed OpenVDB file per selected frame with sparse conditioned `density`, `temperature`,
`emission`, and optional `vel` grids, non-uniform index-to-world transforms, timing/units/provenance metadata, and a
JSON manifest. New Export Studio sessions select all four grids by default. `vel` requires a source cache baked with
**Store Velocity**; it may be omitted only when downstream motion, up-res detail, and fluid-following particles are not
needed.
This numbered-file layout is the standard OpenVDB animation convention: one `.vdb` stores one time sample (and may
contain several grids), while the sequence/manifest forms the clip. The standalone **Braze VDB Import** component under
`BrazeFX > Baking` accepts the manifest or any numbered frame from that sequence as a complete animated clip; a
standalone `.vdb` remains a still. Choosing the source automatically converts it into the internal streamed playback
data and shows its first frame, without exposing cache files or requiring a second import action.
No second playback component or user-authored output path is required; runtime data is generated automatically under
`Assets/BrazeFX Imports`. Import and Cache Player share a neutral playback core rather than one component depending on
the other. Import is kept out of Export Studio. The sibling **Braze Export Studio** component binds a source Container and
opens Export Studio for delivery.
The Import inspector previews through the real scene renderer and can play/pause/stop/restart,
step or scrub exact stored frames, choose streaming or whole-clip playback, set realtime
raymarch limits and lighting, and optionally re-grade the complete imported look. The same transport is available at
runtime through `LoadCache`, `Play`, `Pause`, `Stop`, `Restart`, `SeekFrame`, `SeekSeconds`, `Seek01`, and `StepFrames`;
status, playback-state, and delivered-frame changes expose events for gameplay integration.
When a VDB contains `vel`, import preserves its signed vector values in grid voxels/second and playback scatters that
field directly into the GPU velocity volume. It is the authoritative source for curl, motion-aware Render Detail,
up-res flow, and connected Braze particle systems; the old adjacent-scalar optical-flow solve is bypassed. Particle
connections are advection-only, so each target keeps its own duration, looping, rate, bursts, and spawn shape (a Pyro
Emitter may still own spawning while the VDB owns lifetime fluid follow). Velocity works for stills as well as clips.
When `vel` is absent, animated scalar-only clips retain the deterministic `BrazeVdbPlayback.compute` optical-flow
fallback based on adjacent density, temperature, and emission frames. A scalar-only still has neutral fluid motion.
The renderer-motion option exposes only Detail Flow Memory for authoritative velocity; optical-flow refinement,
coherence, temporal smoothing, and full-resolution controls appear only for scalar fallback.
Braze-authored gains are inverted during import, and manifest v3 carries the complete baked renderer look and resolved
key light; older local manifests recover those settings from their fingerprint-matched source cache when available.
The OpenVDB card owns its Folder and Name independently from Flipbook, derives loop sources from the authored Region,
and always writes physically correct metre transforms. New and migrated prerelease sessions default to
`BrazeFX Exports/VDB Exports` outside `Assets`, preventing Unity's Asset Database from redundantly importing every DCC
delivery frame; custom folders remain supported. Native Render Detail bricks remain renderer-only and are not mixed
into the base interchange grids. Import and export are publisher-owned managed Editor operations with no native
OpenVDB/oneTBB plugin or external installation.

Exports use staging and rollback, preserve previous valid outputs on cancellation/failure, record bounded job history,
recover abandoned staging after reload, estimate memory/disk cost, and fingerprint the baked source/cache. See the
customer guides in `Documentation` for complete workflows and examples.

## Important files

| File | Role |
|------|------|
| `Resources/BrazeSolver.compute` | All solver kernels. |
| `Resources/BrazeVdbPlayback.compute` | VDB-only adjacent-frame renderer motion/curl/flow reconstruction. |
| `Resources/BrazeNoise.hlsl` | Value noise + divergence-free curl noise. |
| `Runtime/BrazeSolver.cs` | RenderTextures, emitter/force ComputeBuffers, dispatch, dispose. |
| `Runtime/BrazeContainer.cs` | Sim domain + render proxy; gathers children. |
| `Runtime/BrazePyroEmitter.cs`, `BrazePyro*Force.cs`, `BrazeSplineVortex.cs`, `BrazeObstacle.cs` | Fluid inputs. Dedicated force components share the legacy-compatible `BrazePyroForce` engine. |
| `Runtime/BrazeContainer.SliceRenderer.cs` | Per-container render resources and live shadow/sculpt bakes. |
| `Runtime/URP/BrazeRenderFeature.cs` | Unified URP renderer feature. |
| `Shaders/Shared/BrazeSlicesCore.hlsl` | Shared live/cache volume sampling and shading. |
| `Runtime/BrazeCache*.cs` | Cache format, bake, resident/streamed sources, and playback. |
| `Editor/BrazeComputeKernelPrecompiler.cs` | One-time Editor kernel preparation that keeps cold compilation out of Play Mode entry. |
| `Editor/BrazeExport*.cs`, `Editor/BrazeFlipbookExporter.cs` | Export Studio state, Scene-view cache host, fingerprints, jobs, and delivery. |
| `Editor/Vdb` | Publisher-owned managed OpenVDB reader/writer used by Editor import and export. |
| `Demos`, `Documentation` | Self-contained examples and customer guides. |

## Notes

- Changing `resolution` at runtime rebuilds safely; an allocation failure leaves the previous solver alive and reports
  the rejected request in the inspector/HUD diagnostics.
- For fire bloom, enable **HDR + Bloom** on the camera/URP asset.
