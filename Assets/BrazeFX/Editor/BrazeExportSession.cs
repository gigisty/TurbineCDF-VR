using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

namespace BrazeFX.EditorTools
{
    /// <summary>
    /// Project-local authoring state for Braze Export Studio. The source Container is serialized as a GlobalObjectId
    /// because editor state cannot safely retain direct scene-object references across reloads and Play Mode changes.
    /// </summary>
    public sealed class BrazeExportSession : ScriptableObject
    {
        public const int CurrentVersion = 13;
        public const float DefaultDuration = 5f;
        public const string DefaultCacheOutputFolder = "BrazeFX Working Caches";
        public const string DefaultVolumeOutputFolder = "BrazeFX Exports/VDB Exports";

        public enum RegionPlayback { Once, Loop, PingPong, CrossfadeLoop }
        // Kept only so v8 and older sessions can migrate their multi-view selection.
        [System.Flags]
        public enum FlipbookViews
        {
            Front = 1 << 0, Back = 1 << 1, Left = 1 << 2, Right = 1 << 3,
            Top = 1 << 4, Bottom = 1 << 5, Isometric = 1 << 6,
        }
        public enum FlipbookProjection { Perspective, Orthographic }
        public enum FlipbookFormat { Png, Tga, Exr }
        public enum FlipbookLayout { Automatic, Manual }
        public enum CachePurpose
        {
            [InspectorName("Full Playback & Flipbook")] FullPlaybackAndFlipbook = 0,
            [InspectorName("VDB Delivery (Lean)")] VdbDelivery = 1,
        }
        [System.Flags]
        public enum VolumeGrids
        {
            Density = 1 << 0,
            Temperature = 1 << 1,
            Emission = 1 << 2,
            Velocity = 1 << 3,   // world-space m/s Vec3 `vel` grid; present in every current Export Studio master
        }
        // NanoVdb remains as value 1 only so prerelease v10 session/job data can be migrated without
        // reinterpreting serialized enum integers. Export Studio v1 exposes and writes OpenVDB only.
        public enum VolumeFormat { OpenVdb = 0, NanoVdb = 1 }
        public enum JobKind { MasterCache, DerivedRegion, Flipbook, OpenVdb, NanoVdb }
        public enum JobState { Queued, Running, Completed, Cancelled, Failed, Interrupted }

        [System.Serializable]
        public sealed class JobRecord
        {
            public string id;
            public JobKind kind;
            public JobState state;
            public string status;
            public float progress;
            public string startedUtc;
            public string finishedUtc;
            public string error;
        }

        [SerializeField, HideInInspector] int version = CurrentVersion;
        [SerializeField, HideInInspector] string sourceContainerGlobalId;
        [System.NonSerialized] BrazeContainer resolvedContainer;

        [Tooltip("The reusable master cache produced by the most recent successful bake.")]
        public BrazeFluidCacheAsset cache;
        [FormerlySerializedAs("derivedLoopCache"), Tooltip("Latest non-destructive region cache derived from the master.")]
        public BrazeFluidCacheAsset derivedRegionCache;
        [HideInInspector] public string derivedRegionFingerprint;
        [HideInInspector] public string bakedSourceFingerprint;
        [HideInInspector] public string bakedCacheFingerprint;
        [HideInInspector] public string lastBakeUtc;
        [SerializeField] public System.Collections.Generic.List<JobRecord> jobs = new();

        [Tooltip("Complete simulation timeline baked into the master cache.")]
        [Min(1f / 240f)] public float duration = DefaultDuration;
        [FormerlySerializedAs("loopIn"), Min(0f), Tooltip("Inclusive start of the authored playback and export region.")]
        public float regionIn;
        [FormerlySerializedAs("loopOut"), Min(1f / 240f), Tooltip("Exclusive end of the authored playback and export region.")]
        public float regionOut = DefaultDuration;
        [FormerlySerializedAs("loopTreatment"), Tooltip("How the playhead behaves inside the authored region.")]
        public RegionPlayback regionPlayback = RegionPlayback.Once;
        [Tooltip("Length of the volume-space blend used by Crossfade Loop.")]
        [Min(0f)] public float crossfadeDuration = 0.25f;

        // LEGACY serialized fields — no longer authored. Normalize() forces storage to lossless Compact, and every
        // master bake is lean (scalars + velocity, Render Detail rebuilt from velocity), so the old purpose split
        // no longer changes anything. Kept only so existing session assets deserialize cleanly.
        [HideInInspector] public BrazeCacheBaker.Storage storage = BrazeCacheBaker.Storage.Compact;
        [HideInInspector] public CachePurpose cachePurpose = CachePurpose.FullPlaybackAndFlipbook;

        [Tooltip("Folder for renderer-ready master and derived working caches. Outside Assets is recommended: the " +
                 "small cache asset stays in Assets while Unity does not import the large binary.")]
        public string cacheOutputFolder = DefaultCacheOutputFolder;

        [Tooltip("Folder for flipbook delivery exports. May be inside or outside Assets.")]
        public string outputFolder = "Assets/BrazeFX Exports";
        [Tooltip("Base name shared by cache, flipbook, and VDB outputs.")]
        public string outputName = "BrazeExport";

        public BrazeFlipbookAsset flipbookAsset;
        [Tooltip("Camera direction rendered into the flipbook atlas.")]
        public BrazeFlipbookAsset.ViewDirection flipbookView = BrazeFlipbookAsset.ViewDirection.Front;
        [SerializeField, HideInInspector] FlipbookViews flipbookViews = FlipbookViews.Front;
        public FlipbookProjection flipbookProjection = FlipbookProjection.Perspective;
        [Tooltip("Per-frame resolution used by Automatic layout. Manual layout derives it from Atlas Size ÷ Columns/Rows.")]
        public Vector2Int flipbookFrameResolution = new(512, 512);
        [Range(0f, 0.5f)] public float flipbookPadding = 0.08f;
        public FlipbookLayout flipbookLayout = FlipbookLayout.Automatic;
        [Tooltip("Automatic: maximum atlas width/height. Manual: exact atlas width/height.")]
        public Vector2Int flipbookMaxAtlasResolution = new(8192, 8192);
        [Min(1)] public int flipbookColumns = 16;
        [Min(1)] public int flipbookRows = 16;
        [SerializeField, HideInInspector] int flipbookMaxAtlasSize = 8192; // v6 square-cap migration source
        public FlipbookFormat flipbookFormat = FlipbookFormat.Png;
        public Color flipbookBackground = new(0f, 0f, 0f, 0f);
        public bool flipbookPremultipliedAlpha = true;
        [Range(0, 16)] public int flipbookAlphaDilation = 2;
        public bool flipbookAutoBounds = true;
        public Vector3 flipbookBoundsCenter;
        public Vector3 flipbookBoundsSize = Vector3.one;
        public Vector3 flipbookPivot;

        [FormerlySerializedAs("volumeUseDerivedLoop"), HideInInspector]
        public bool volumeUseDerivedRegion = true; // v9 compatibility; the authored Region now owns derivation.
        [Tooltip("Folder for OpenVDB sequences. Outside Assets is recommended so Unity does not import every delivery frame as a project asset.")]
        public string volumeOutputFolder = DefaultVolumeOutputFolder;
        [Tooltip("Base name used by OpenVDB frame sequences and their manifest.")]
        public string volumeOutputName = "BrazeExport";
        public VolumeFormat volumeFormat = VolumeFormat.OpenVdb;
        public VolumeGrids volumeGrids = VolumeGrids.Density | VolumeGrids.Temperature |
                                         VolumeGrids.Emission | VolumeGrids.Velocity;
        [HideInInspector]
        public string volumeUnits = "meters";
        [Tooltip("Voxels at or below this value are not activated in the output sparse grids.")]
        [Min(0f)] public float volumeBackgroundThreshold = 0.0001f;

        public int Version => version;
        public BrazeContainer SourceContainer => ResolveObject(sourceContainerGlobalId, ref resolvedContainer);
        public string SourceContainerGlobalId => sourceContainerGlobalId;
        public int BakeFps => SourceContainer != null ? SourceContainer.CacheBakeFps : 30;
        public float FrameDuration => 1f / Mathf.Max(1, BakeFps);
        public int CaptureFrameCount => Mathf.Max(1, Mathf.RoundToInt(duration * BakeFps));
        public int RegionStartFrame => Mathf.Clamp(
            Mathf.RoundToInt(regionIn * BakeFps), 0, Mathf.Max(0, CaptureFrameCount - 1));
        public int RegionFrameCount => Mathf.Clamp(
            Mathf.Max(1, Mathf.RoundToInt((regionOut - regionIn) * BakeFps)), 1,
            Mathf.Max(1, CaptureFrameCount - RegionStartFrame));
        public bool RegionLoops => regionPlayback != RegionPlayback.Once;
        public bool RegionNeedsDerivedCache => regionPlayback is RegionPlayback.PingPong or RegionPlayback.CrossfadeLoop;
        // Derived cache selection is a consequence of the Region playback mode, not a separate delivery choice.
        public bool FlipbookUsesDerivedRegion => RegionNeedsDerivedCache;
        public bool VolumeUsesDerivedRegion => RegionNeedsDerivedCache;

        public string BeginJob(JobKind kind, string initialStatus)
        {
            jobs ??= new System.Collections.Generic.List<JobRecord>();
            while (jobs.Count >= 12) jobs.RemoveAt(0);
            var record = new JobRecord
            {
                id = System.Guid.NewGuid().ToString("N"),
                kind = kind,
                state = JobState.Running,
                status = initialStatus ?? string.Empty,
                progress = 0f,
                startedUtc = System.DateTime.UtcNow.ToString("O"),
            };
            jobs.Add(record);
            Persist();
            return record.id;
        }

        public void FinishJob(string id, JobState state, string finalStatus, string errorMessage = null)
        {
            if (jobs == null || string.IsNullOrEmpty(id)) return;
            JobRecord record = jobs.Find(value => value != null && value.id == id);
            if (record == null) return;
            record.state = state;
            record.status = finalStatus ?? string.Empty;
            record.progress = state == JobState.Completed ? 1f : Mathf.Clamp01(record.progress);
            record.error = errorMessage ?? string.Empty;
            record.finishedUtc = System.DateTime.UtcNow.ToString("O");
            Persist();
        }

        public void UpdateJob(string id, string currentStatus, float currentProgress)
        {
            if (jobs == null || string.IsNullOrEmpty(id)) return;
            JobRecord record = jobs.Find(value => value != null && value.id == id);
            if (record == null || record.state != JobState.Running) return;
            record.status = currentStatus ?? record.status;
            record.progress = Mathf.Clamp01(currentProgress);
        }

        void OnEnable()
        {
            ResolveSources();
            if (Normalize()) EditorUtility.SetDirty(this);
            RecoverInterruptedJobs();
        }
        void OnValidate() => Normalize();

        void RecoverInterruptedJobs()
        {
            if (jobs == null || BrazeCacheBakeDriver.Active) return;
            bool changed = false;
            for (int i = 0; i < jobs.Count; i++)
            {
                JobRecord record = jobs[i];
                if (record == null || (record.state != JobState.Running && record.state != JobState.Queued)) continue;
                record.state = JobState.Interrupted;
                record.status = "Interrupted by an Editor restart or domain reload";
                record.finishedUtc = System.DateTime.UtcNow.ToString("O");
                changed = true;
            }
            if (changed) EditorUtility.SetDirty(this);
        }

        public void BindContainer(BrazeContainer value)
        {
            resolvedContainer = value;
            sourceContainerGlobalId = ObjectId(value);
        }

        public void ResolveSources()
        {
            resolvedContainer = ResolveObject<BrazeContainer>(sourceContainerGlobalId);
        }

        internal void Persist() => BrazeExportSessionStore.Save(this);

        /// <summary>Repair non-finite, unordered, or off-frame authoring values.</summary>
        public bool Normalize()
        {
            float oldDuration = duration;
            float oldRegionIn = regionIn;
            float oldRegionOut = regionOut;
            float oldCrossfade = crossfadeDuration;
            int oldVersion = version;
            BrazeCacheBaker.Storage oldStorage = storage;
            CachePurpose oldCachePurpose = cachePurpose;
            string oldCacheFolder = cacheOutputFolder;
            string oldFolder = outputFolder;
            string oldName = outputName;
            Vector2Int oldFlipbookResolution = flipbookFrameResolution;
            FlipbookLayout oldFlipbookLayout = flipbookLayout;
            Vector2Int oldAtlasResolution = flipbookMaxAtlasResolution;
            int oldColumns = flipbookColumns;
            int oldRows = flipbookRows;
            BrazeFlipbookAsset.ViewDirection oldFlipbookView = flipbookView;
            float oldPadding = flipbookPadding;
            int oldDilation = flipbookAlphaDilation;
            Vector3 oldBoundsCenter = flipbookBoundsCenter;
            Vector3 oldBoundsSize = flipbookBoundsSize;
            Vector3 oldPivot = flipbookPivot;
            VolumeFormat oldVolumeFormat = volumeFormat;
            VolumeGrids oldVolumeGrids = volumeGrids;
            string oldVolumeFolder = volumeOutputFolder;
            string oldVolumeName = volumeOutputName;
            string oldVolumeUnits = volumeUnits;
            float oldVolumeThreshold = volumeBackgroundThreshold;

            if (oldVersion < 4) derivedRegionCache = null;
            if (oldVersion < 7)
            {
                int legacyAtlas = Mathf.Clamp(flipbookMaxAtlasSize, 256, 16384);
                flipbookMaxAtlasResolution = new Vector2Int(legacyAtlas, legacyAtlas);
            }
            if (oldVersion < 8 && flipbookLayout == FlipbookLayout.Manual)
            {
                // v7 Manual mode edited both Cell Size and Columns/Rows. Convert that over-specified state into
                // the exact atlas those inputs described; v8 then derives the cell from Atlas Size ÷ grid.
                flipbookMaxAtlasResolution = new Vector2Int(
                    ClampAtlasAxis((long)flipbookColumns * flipbookFrameResolution.x),
                    ClampAtlasAxis((long)flipbookRows * flipbookFrameResolution.y));
            }
            if (oldVersion < 9) flipbookView = FirstLegacyView(flipbookViews);
            if (oldVersion < 10)
            {
                volumeOutputFolder = outputFolder;
                volumeOutputName = outputName;
            }
            if (oldVersion < 12)
            {
                string legacyVolumeFolder = volumeOutputFolder?.Trim().Replace('\\', '/');
                const string legacyRoot = "Assets/BrazeFX Exports";
                if (string.Equals(legacyVolumeFolder, legacyRoot, System.StringComparison.OrdinalIgnoreCase) ||
                    legacyVolumeFolder != null && legacyVolumeFolder.StartsWith(legacyRoot + "/",
                        System.StringComparison.OrdinalIgnoreCase))
                    volumeOutputFolder = legacyVolumeFolder.Substring("Assets/".Length);
            }

            int fps = Mathf.Max(1, BakeFps);
            float step = 1f / fps;
            duration = Mathf.Max(step, SnapFinite(duration, DefaultDuration, fps));

            regionIn = Mathf.Clamp(SnapFinite(regionIn, 0f, fps), 0f, Mathf.Max(0f, duration - step));
            regionOut = Mathf.Clamp(SnapFinite(regionOut, duration, fps), regionIn + step, duration);
            crossfadeDuration = Mathf.Clamp(FiniteOr(crossfadeDuration, 0f), 0f,
                Mathf.Max(0f, (regionOut - regionIn) * 0.5f));
            // The master has exactly ONE form: lossless Compact, scalars + velocity. It is simultaneously the exact
            // scrub/flipbook source and the authoritative OpenVDB source. Legacy sessions that authored Fast or
            // Realtime working storage migrate here unconditionally; realtime game clips belong to the Cache Baker.
            storage = BrazeCacheBaker.Storage.Compact;
            // Purpose is retained only as a deserialization bridge. Canonicalize every value so a hidden legacy
            // VDB-only selection cannot keep changing provenance fingerprints after the authoring choice was removed.
            cachePurpose = CachePurpose.FullPlaybackAndFlipbook;
            cacheOutputFolder = string.IsNullOrWhiteSpace(cacheOutputFolder)
                ? DefaultCacheOutputFolder : cacheOutputFolder.Trim();
            outputFolder = string.IsNullOrWhiteSpace(outputFolder) ? "Assets/BrazeFX Exports" : outputFolder.Trim();
            outputName = SanitizeFileName(outputName);
            flipbookFrameResolution = new Vector2Int(
                Mathf.Clamp(flipbookFrameResolution.x, 64, 4096),
                Mathf.Clamp(flipbookFrameResolution.y, 64, 4096));
            flipbookLayout = System.Enum.IsDefined(typeof(FlipbookLayout), flipbookLayout)
                ? flipbookLayout : FlipbookLayout.Automatic;
            flipbookMaxAtlasResolution = new Vector2Int(
                Mathf.Clamp(flipbookMaxAtlasResolution.x, 256, 16384),
                Mathf.Clamp(flipbookMaxAtlasResolution.y, 256, 16384));
            flipbookColumns = Mathf.Clamp(flipbookColumns, 1, 4096);
            flipbookRows = Mathf.Clamp(flipbookRows, 1, 4096);
            if (!System.Enum.IsDefined(typeof(BrazeFlipbookAsset.ViewDirection), flipbookView))
                flipbookView = BrazeFlipbookAsset.ViewDirection.Front;
            flipbookPadding = Mathf.Clamp(FiniteOr(flipbookPadding, 0.08f), 0f, 0.5f);
            flipbookAlphaDilation = Mathf.Clamp(flipbookAlphaDilation, 0, 16);
            flipbookBoundsCenter = FiniteVector(flipbookBoundsCenter, Vector3.zero);
            flipbookBoundsSize = new Vector3(
                Mathf.Max(0.001f, FiniteOr(flipbookBoundsSize.x, 1f)),
                Mathf.Max(0.001f, FiniteOr(flipbookBoundsSize.y, 1f)),
                Mathf.Max(0.001f, FiniteOr(flipbookBoundsSize.z, 1f)));
            flipbookPivot = FiniteVector(flipbookPivot, Vector3.zero);
            // The flags dropdown's "Everything" serializes -1; keep only defined bits so no consumer ever sees
            // the undefined ones (an unmasked -1 previously hung the Editor in the window's bit-count loop).
            volumeGrids &= VolumeGrids.Density | VolumeGrids.Temperature | VolumeGrids.Emission | VolumeGrids.Velocity;
            if (volumeGrids == 0) volumeGrids = VolumeGrids.Density;
            // NanoVDB was a prerelease native-only option. Preserve its serialized enum value above, but migrate
            // every saved session to the dependency-free OpenVDB delivery path for the v1 product.
            volumeFormat = VolumeFormat.OpenVdb;
            volumeOutputFolder = string.IsNullOrWhiteSpace(volumeOutputFolder)
                ? DefaultVolumeOutputFolder : volumeOutputFolder.Trim();
            volumeOutputName = SanitizeFileName(volumeOutputName);
            // Unity world units are metres. Relabelling the unchanged transform as another unit makes the VDB
            // physically incorrect, so the interchange contract deliberately uses metres only.
            volumeUnits = "meters";
            volumeBackgroundThreshold = Mathf.Clamp(FiniteOr(volumeBackgroundThreshold, 0.0001f), 0f, 1f);
            version = CurrentVersion;

            return oldDuration != duration || oldRegionIn != regionIn || oldRegionOut != regionOut ||
                   oldCrossfade != crossfadeDuration ||
                   oldVersion != version ||
                   oldStorage != storage || oldCachePurpose != cachePurpose ||
                   oldCacheFolder != cacheOutputFolder ||
                   oldFolder != outputFolder || oldName != outputName ||
                   oldFlipbookResolution != flipbookFrameResolution ||
                   oldFlipbookLayout != flipbookLayout || oldAtlasResolution != flipbookMaxAtlasResolution ||
                   oldColumns != flipbookColumns || oldRows != flipbookRows ||
                   oldFlipbookView != flipbookView || oldPadding != flipbookPadding ||
                   oldDilation != flipbookAlphaDilation || oldBoundsCenter != flipbookBoundsCenter ||
                   oldBoundsSize != flipbookBoundsSize || oldPivot != flipbookPivot ||
                   oldVolumeFormat != volumeFormat || oldVolumeGrids != volumeGrids ||
                   oldVolumeFolder != volumeOutputFolder ||
                   oldVolumeName != volumeOutputName || oldVolumeUnits != volumeUnits ||
                   oldVolumeThreshold != volumeBackgroundThreshold;
        }

        public float SnapTime(float value) => Mathf.Round(FiniteOr(value, 0f) * BakeFps) / Mathf.Max(1, BakeFps);

        static string ObjectId(Object value)
            => value == null ? string.Empty : GlobalObjectId.GetGlobalObjectIdSlow(value).ToString();

        static T ResolveObject<T>(string id) where T : Object
        {
            if (string.IsNullOrEmpty(id) || !GlobalObjectId.TryParse(id, out GlobalObjectId globalId)) return null;
            return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId) as T;
        }

        static T ResolveObject<T>(string id, ref T cached) where T : Object
        {
            if (cached == null) cached = ResolveObject<T>(id);
            return cached;
        }

        static float SnapFinite(float value, float fallback, int fps)
            => Mathf.Round(FiniteOr(value, fallback) * fps) / Mathf.Max(1, fps);

        static float FiniteOr(float value, float fallback) => float.IsFinite(value) ? value : fallback;

        static int ClampAtlasAxis(long value)
            => (int)System.Math.Min(16384L, System.Math.Max(256L, value));

        static BrazeFlipbookAsset.ViewDirection FirstLegacyView(FlipbookViews views)
        {
            for (int index = 0; index <= (int)BrazeFlipbookAsset.ViewDirection.Isometric; index++)
                if ((((int)views) & (1 << index)) != 0)
                    return (BrazeFlipbookAsset.ViewDirection)index;
            return BrazeFlipbookAsset.ViewDirection.Front;
        }

        static Vector3 FiniteVector(Vector3 value, Vector3 fallback) => new(
            FiniteOr(value.x, fallback.x), FiniteOr(value.y, fallback.y), FiniteOr(value.z, fallback.z));

        static string SanitizeFileName(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "BrazeExport" : value.Trim();
            const string invalid = "/\\:*?\"<>|";
            for (int i = 0; i < invalid.Length; i++) value = value.Replace(invalid[i], '_');
            return string.IsNullOrWhiteSpace(value) ? "BrazeExport" : value;
        }
    }
}
