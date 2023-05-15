using System;
using UnityEngine;

#if ENABLE_WINMD_SUPPORT
using Windows.Foundation;
using Windows.Perception.Spatial;
using SpatialAnchor = Windows.Perception.Spatial.SpatialAnchor;
#else
using SpatialAnchor = System.Object;
#endif

namespace Microsoft.Azure.SpatialAnchors
{
    /// <summary>
    /// <c>ASALocalAnchor</c> represents a spatial anchor not associated with the cloud.
    /// It is currently backed by a WinMR anchor, but is not tied to a game object unlike ARAnchor.
    /// </summary>
    public class ASALocalAnchor
    {
        private SpatialAnchor _spatialAnchor;

        internal SpatialAnchor SpatialAnchor => _spatialAnchor;

#if ENABLE_WINMD_SUPPORT
        /// <summary>
        /// Warning: This callback does not work with SpatialAnchors that are created through the app
        /// right now. Only seems to work for SpatialAnchors created through ARFoundation.
        /// </summary>
        internal event TypedEventHandler<SpatialAnchor, SpatialAnchorRawCoordinateSystemAdjustedEventArgs> RawCoordinateSystemAdjusted
        {
            add
            {
                SpatialAnchor.RawCoordinateSystemAdjusted += value;
            }
            remove
            {
                SpatialAnchor.RawCoordinateSystemAdjusted += value;
            }
        }
#endif

        /// <summary>
        /// Constructs a local anchor object from the underlying Mirage SpatialAnchor.
        /// </summary>
        /// <param name="spatialAnchor">Runtime Callable Wrapper for the Mirage SpatialAnchor</param>
        public ASALocalAnchor(SpatialAnchor spatialAnchor)
        {
            if (spatialAnchor == null)
            {
                throw new ArgumentException($"SpatialAnchor object for local anchor is null");
            }

            _spatialAnchor = spatialAnchor;
        }

        /// <summary>
        /// Attempts to compute the anchor to Unity world pose of this local anchor.
        /// </summary>
        /// <remarks>
        /// This function is expensive and should only be used once to establish a local ARAnchor
        /// for a game object.
        /// </remarks>
        /// <returns>The pose from the anchor to the Unity world origin, if available</returns>
        public Pose? TryComputeAnchorToWorldPose()
        {
            Pose? anchorToWorldPose = TryComputeAnchorToWorldPoseMatrix()?.ToUnityPose();
            return anchorToWorldPose;
        }

        internal System.Numerics.Matrix4x4? TryComputeAnchorToWorldPoseMatrix()
        {
            System.Numerics.Matrix4x4? anchorToWorldPoseMatrix =
                WinMRInterop.TryComputeAnchorToWorldPose(_spatialAnchor);
            return anchorToWorldPoseMatrix;
        }
    }
}
