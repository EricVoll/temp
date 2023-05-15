using Microsoft.Azure.SpatialAnchors.Unity;
using System;
using System.Numerics;

#if ENABLE_WINMD_SUPPORT
using Windows.Perception.Spatial;
using System.Runtime.InteropServices;
using Debug = UnityEngine.Debug;

#if ASA_UNITY_USE_OPENXR
using Microsoft.MixedReality.OpenXR;
#else
using UnityEngine.XR.WindowsMR;
#endif
using SpatialAnchor = Windows.Perception.Spatial.SpatialAnchor;
#else
using SpatialAnchor = System.Object;
#endif

namespace Microsoft.Azure.SpatialAnchors
{
    internal static class WinMRInterop
    {
#if ENABLE_WINMD_SUPPORT
        // Used to cache per-frame computations.
        private static int s_lastUnityWorldCoordinateSystemRefreshFrame = -1;
        private static SpatialCoordinateSystem s_cachedUnityWorldCoordinateSystem;
#endif

        /// <summary>
        /// This function computes the pose between the local SpatialAnchor and the WinMR OriginSpatialCoordinateSystem.
        /// Warning: This can take several ms to compute so do not call this on the Update loop for every anchor.
        /// </summary>
        internal static System.Numerics.Matrix4x4? TryComputeAnchorToWorldPose(SpatialAnchor spatialAnchor)
        {
            System.Numerics.Matrix4x4? anchorToWorldPose = null;
#if ENABLE_WINMD_SUPPORT
            SpatialCoordinateSystem unityWorldCoordinateSystem = GetUnityWorldCoordinateSystem();
            if (unityWorldCoordinateSystem == null)
            {
                return null;
            }

            // Get the SpatialCoordinateSystem reference for the underlying SpatialAnchor
            SpatialCoordinateSystem anchorCoordinateSystem = spatialAnchor.CoordinateSystem;

            // Get the transform for converting the anchor's coordinate system to the Unity world coordinate system
            anchorToWorldPose = anchorCoordinateSystem.TryGetTransformTo(unityWorldCoordinateSystem);
#endif
            return anchorToWorldPose;
        }

#if ENABLE_WINMD_SUPPORT
        internal static SpatialAnchor TryCreateAnchor(Matrix4x4 anchorToWorldPose)
        {
            SpatialCoordinateSystem unityWorldCoordinateSystem = GetUnityWorldCoordinateSystem();
            if (unityWorldCoordinateSystem == null)
            {
                return null;
            }

            return SpatialAnchor.TryCreateRelativeTo(
                unityWorldCoordinateSystem,
                anchorToWorldPose.Translation,
                Quaternion.CreateFromRotationMatrix(anchorToWorldPose));
        }

        /// <summary>
        /// Get the world coordinate frame according to Unity as a SpatialCoordinateSystem
        /// </summary>
        /// <remarks>May only be called from the main thread</remarks>
        internal static SpatialCoordinateSystem GetUnityWorldCoordinateSystem()
        {
            int currentFrame = UnityEngine.Time.frameCount;
            if (s_lastUnityWorldCoordinateSystemRefreshFrame != currentFrame)
            {
                s_cachedUnityWorldCoordinateSystem = FetchUnityWorldCoordinateSystem();
                s_lastUnityWorldCoordinateSystemRefreshFrame = currentFrame;
            }
            return s_cachedUnityWorldCoordinateSystem;
        }

        /// <summary>
        /// Re-computes the world coordinate frame according to Unity as a SpatialCoordinateSystem
        /// </summary>
        private static SpatialCoordinateSystem FetchUnityWorldCoordinateSystem()
        {
#if ASA_UNITY_USE_OPENXR
            return PerceptionInterop.GetSceneCoordinateSystem(UnityEngine.Pose.identity) as SpatialCoordinateSystem;
#else
            return Marshal.GetObjectForIUnknown(WindowsMREnvironment.OriginSpatialCoordinateSystem) as SpatialCoordinateSystem;
#endif
        }

        /// <summary>
        /// This function computes the pose between the local SpatialAnchor and the WinMR or OpenXR OriginSpatialCoordinateSystem.
        /// </summary>
        internal static bool TryComputeAnchorToWorldPose(
            SpatialAnchor spatialAnchor,
            SpatialLocator spatialLocator,
            out System.Numerics.Matrix4x4? anchorToWorldPoseMatrix)
        {
            if (spatialLocator.Locatability != SpatialLocatability.PositionalTrackingActive)
            {
                anchorToWorldPoseMatrix = null;
                return false;
            }

            SpatialCoordinateSystem unityWorldCoordinateSystem = GetUnityWorldCoordinateSystem();
            if (unityWorldCoordinateSystem == null)
            {
                Debug.Log("Could not get pose to Unity World!");
                anchorToWorldPoseMatrix = null;
                return false;
            }

            // Get the SpatialCoordinateSystem reference for the underlying SpatialAnchor
            SpatialCoordinateSystem anchorCoordinateSystem = spatialAnchor.CoordinateSystem;

            anchorToWorldPoseMatrix = anchorCoordinateSystem.TryGetTransformTo(unityWorldCoordinateSystem);
            return anchorToWorldPoseMatrix.HasValue;
        }
#endif

        internal static Matrix4x4 ToNumericsPose(this UnityEngine.Pose pose)
        {
            var unityPoseMatrix = pose.ToMatrix();
            Matrix4x4 numericsPoseMatrix = ASAUnityExtensions.ConvertToRightHandedRowVectorMatrix(unityPoseMatrix);
            return numericsPoseMatrix;
        }

        internal static UnityEngine.Pose ToUnityPose(this Matrix4x4 pose)
        {
            var (position, rotation) = pose.ToTranslationRotationTuple();

            UnityEngine.Pose anchorPose = new UnityEngine.Pose()
            {
                position = new UnityEngine.Vector3(position.X, position.Y, position.Z),
                rotation = new UnityEngine.Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W)
            };

            return anchorPose;
        }

        internal static Tuple<Vector3, Vector4> ToTranslationRotationTuple(
            this Matrix4x4 anchorToWorld)
        {
            // Transform the pose to the Unity world coordinate system
            var unityWorldTranslation = new Vector3(
                anchorToWorld.Translation.X,
                anchorToWorld.Translation.Y,
                -anchorToWorld.Translation.Z);

            var anchorToUnityWorldRotationNative = Quaternion.CreateFromRotationMatrix(anchorToWorld);
            var unityWorldRotation = new Vector4(-anchorToUnityWorldRotationNative.X,
                -anchorToUnityWorldRotationNative.Y,
                anchorToUnityWorldRotationNative.Z,
                anchorToUnityWorldRotationNative.W);

            // Return the pose as a translation and rotation pair
            var unityWorldPose = new Tuple<Vector3, Vector4>(unityWorldTranslation, unityWorldRotation);

            return unityWorldPose;
        }
    }
}
