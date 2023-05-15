using System;
using System.Numerics;
using Unity.Profiling;

#if ENABLE_WINMD_SUPPORT
using Windows.Perception.Spatial;
#endif

namespace Microsoft.Azure.SpatialAnchors
{
    /// <summary>
    /// <c>ASAAnchor</c> references a persistent cloud anchor in a map.
    /// </summary>
    public class ASAAnchor
    {
        /// <summary>
        /// Returns the UUID for the underlying <c>CloudSpatialAnchor</c>.
        /// </summary>
        public Guid Identifier => _identifier;

        public AnchorLocatabilityStatus Locatability
        {
            get
            {
                EnsurePoseAndLocatabilityRefreshed();
                return _locatability;
            }
        }

        /// <summary>
        /// Returns the ASALocalAnchor wrapping the Mirage SpatialAnchor pointer from the underlying
        /// <c>CloudSpatialAnchor</c>.
        /// </summary>
        internal ASALocalAnchor LocalAnchor => _localAnchor;

        /// <summary>
        /// Pose relating the cloud anchor to the local anchor.
        /// </summary>
        internal Matrix4x4 CloudAnchorToLocalAnchorPose => _cloudAnchorToLocalAnchorPose;

        private Matrix4x4 _cloudAnchorToLocalAnchorPose;
        private Matrix4x4? _lastPoseToWorld = null;
        private Guid _identifier = Guid.Empty;
        private ASALocalAnchor _localAnchor = null;

#if ENABLE_WINMD_SUPPORT
        internal SpatialLocator _spatialLocator = null;
#endif

        // Used to cache per-frame computations.
        private int _lastPoseAndLocatabilityRefreshFrame = -1;
        private AnchorLocatabilityStatus _locatability;

        private static readonly ProfilerMarker s_FetchLatestPoseMarker = new ProfilerMarker("ASASession.FetchLatestPose");

#if ENABLE_WINMD_SUPPORT
        /// <summary>
        /// The only valid constructor for an ASAAnchor takes a <c>SpatialLocator</c> as input.
        /// </summary>
        /// <param name="identifier">anchor identifier handed back from the ASA SDK</param>
        /// <param name="localAnchor">wrapper around a Mirage SpatialAnchor</param>
        /// <param name="cloudAnchorToLocalAnchorPose">Pose relating the cloud anchor to the local anchor</param>
        internal ASAAnchor(string identifier, ASALocalAnchor localAnchor, Matrix4x4 cloudAnchorToLocalAnchorPose, SpatialLocator spatialLocator)
        {
            _spatialLocator = spatialLocator;
            if (!Guid.TryParse(identifier, out _identifier))
            {
                throw new ArgumentException("CloudSpatialAnchor's identifier is invalid");
            }

            _identifier = new Guid(identifier);
            _localAnchor = localAnchor;
            _cloudAnchorToLocalAnchorPose = cloudAnchorToLocalAnchorPose;
        }
#endif
        public ASAAnchor(Guid guid, Matrix4x4 lastPoseToWorld)
        {
            _lastPoseToWorld = lastPoseToWorld;
            _identifier = guid;
            _localAnchor = null;
            _locatability = AnchorLocatabilityStatus.Locatable;
        }

        /// <summary>
        /// Only present for compilability without ENABLE_WINMD_SUPPORT
        /// </summary>
        /// <param name="identifier">anchor identifier handed back from the ASA SDK</param>
        /// <param name="localAnchor">wrapper around a Mirage SpatialAnchor</param>
        internal ASAAnchor(string identifier, ASALocalAnchor localAnchor, Matrix4x4 spatialAnchorToLocalAnchorPose)
        {
            throw new NotImplementedException("Must provide SpatialLocator, only works with ENABLE_WINMD_SUPPORT");
        }

        /// <summary>
        /// Tries to gets the pose of the anchor in the Unity world coordinate system. This pose could be 
        /// adjusted as often as every frame, so it should not be cached and used later. If the anchor is not 
        /// currently locatable, this method will return false, and set the out parameter to identity.
        /// </summary>
        /// <param name="anchorToWorldPose">Returns the anchor to world pose in this out parameter</param>
        /// <returns>a <c>Pose</c> representing the translation and rotation</returns>
        public bool TryGetCurrentAnchorToWorldPose(out UnityEngine.Pose anchorToWorldPose)
        {
            if (Locatability != AnchorLocatabilityStatus.Locatable)
            {
                anchorToWorldPose = UnityEngine.Pose.identity;
                return false;
            }

            // If the anchor is currently locatable, then GetLastAnchorToWorldPose will not throw.
            anchorToWorldPose = GetLastAnchorToWorldPose().ToUnityPose();
            return true;
        }

        /// <summary>
        /// Returns the cached pose of the local SpatialAnchor
        /// </summary>
        /// <returns>a matrix representing the pose from the anchor to the world origin</returns>
        internal Matrix4x4 GetLastAnchorToWorldPose()
        {
            EnsurePoseAndLocatabilityRefreshed();

            if (_lastPoseToWorld == null) throw new InvalidOperationException($"SpatialAnchor {_identifier} has not yet been located in this session");

            return _lastPoseToWorld.Value;
        }

        /// <summary>
        /// Sets the local anchor and invalidates the cached pose.
        /// </summary>
        /// <param name="newLocalAnchor">local anchor to set</param>
        internal void SetLocalAnchor(ASALocalAnchor newLocalAnchor, Matrix4x4 cloudAnchorToNewLocalAnchorPose)
        {
            _localAnchor = newLocalAnchor;
            _cloudAnchorToLocalAnchorPose = cloudAnchorToNewLocalAnchorPose;

            // Invalidate cached pose.
            _lastPoseAndLocatabilityRefreshFrame = -1;
        }

        private void EnsurePoseAndLocatabilityRefreshed()
        {
            int currentFrame = UnityEngine.Time.frameCount;
            if (_lastPoseAndLocatabilityRefreshFrame == currentFrame)
            {
                return;
            }

            _lastPoseAndLocatabilityRefreshFrame = currentFrame;

            UpdateLocatabilityAndPoseToWorld();
        }

        private void UpdateLocatabilityAndPoseToWorld()
        {
            if (_localAnchor == null)
            {
                _locatability = AnchorLocatabilityStatus.NotLocatable;
                return;
            }

            using (s_FetchLatestPoseMarker.Auto())
            {
#if ENABLE_WINMD_SUPPORT

                if (_spatialLocator == null)
                {
                    throw new InvalidOperationException("SpatialLocator must be set on ASAAnchor instance");
                }

                // Get the transform for converting the anchor's coordinate system to the Unity world coordinate system
                if (WinMRInterop.TryComputeAnchorToWorldPose(
                    _localAnchor.SpatialAnchor,
                    _spatialLocator,
                    out Matrix4x4? localAnchorToWorldPoseMatrix))
                {
                    _lastPoseToWorld = _cloudAnchorToLocalAnchorPose * localAnchorToWorldPoseMatrix;
                    _locatability = AnchorLocatabilityStatus.Locatable;
                    return;
                }
#endif
                // Leaving _latestPoseToWorld set to the last known world pose of anchor.
                _locatability = AnchorLocatabilityStatus.NotLocatable;
            }
        }
    }
}
