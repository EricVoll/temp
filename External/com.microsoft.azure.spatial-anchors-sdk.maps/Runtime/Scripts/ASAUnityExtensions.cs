using System;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

#if ASA_UNITY_USE_OPENXR
using Microsoft.MixedReality.OpenXR;
#endif
#if ENABLE_WINMD_SUPPORT
using SpatialAnchor = Windows.Perception.Spatial.SpatialAnchor;
#else
using SpatialAnchor = System.Object;
#endif

namespace Microsoft.Azure.SpatialAnchors.Unity
{
    /// <summary>
    /// Data structure used to access the underlying Mirage SpatialAnchor pointer of an
    /// ARFoundation ARAnchor when used on the HoloLens platform
    /// </summary>
    [StructLayout(layoutKind: LayoutKind.Sequential)]
    struct UnityPointer
    {
        /// <summary>
        /// ARFoundation's internal reference for the ARAnchor
        /// </summary>
        public IntPtr UnityInternal;

        /// <summary>
        /// The Mirage SpatialAnchor pointer (when deployed on a HoloLens)
        /// </summary>
        public IntPtr SpatialAnchorPointer;
    }

    /// <summary>
    /// Set of Unity extensions. There is a separate set of extensions at the native level.
    /// </summary>
    public static class ASAUnityExtensions
    {
        /// <summary>
        /// These extension methods have been copied from the ASA SDK 2.x Unity core plugin. They
        /// are used to obtain access to the underlying Mirage SpatialAnchor pointer of an
        /// ARFoundation ARAnchor. Because the SpatialAnchor pointer is not available immediately
        /// after creation, these extensions help the application check the status.
        /// </summary>
        static internal SpatialAnchor GetSpatialAnchorFromNativePtr(this IntPtr intPtr)
        {
            if (intPtr == IntPtr.Zero)
            {
                return null;
            }

#if ASA_UNITY_USE_OPENXR
            return (SpatialAnchor)AnchorConverter.ToPerceptionSpatialAnchor(intPtr);
#else
            UnityPointer pointerGetter = Marshal.PtrToStructure<UnityPointer>(intPtr);
            return (SpatialAnchor)Marshal.GetObjectForIUnknown(pointerGetter.SpatialAnchorPointer);
#endif
        }

        static internal SpatialAnchor GetLocalAnchorAsSpatialAnchorObject(this CloudSpatialAnchor cloudSpatialAnchor)
        {
            using var localAnchorHandle = new ComSafeHandle(cloudSpatialAnchor.LocalAnchor);
            if (localAnchorHandle.IsInvalid)
            {
                return null;
            }

            var spatialAnchor = (SpatialAnchor)localAnchorHandle.GetRuntimeCallableWrapper();
            return spatialAnchor;
        }

        /// <summary>
        /// These extension methods have been copied from the ASA SDK 2.x Unity core plugin. They
        /// are used to obtain access to the underlying Mirage SpatialAnchor pointer of an
        /// ARFoundation ARAnchor. Because the SpatialAnchor pointer is not available immediately
        /// after creation, these extensions help the application check the status.
        /// </summary>
        static internal async Task<SpatialAnchor> GetSpatialAnchorAsync(this ARAnchor anchor)
        {
            if (anchor == null) { throw new ArgumentNullException(nameof(anchor)); }

            // Wait up to one second until AR Foundation creates a Mirage SpatialAnchor, which
            // normally takes about one frame time or around 30 milliseconds
            int retryCount = 30;
            while (anchor.pending && (retryCount > 0))
            {
                await Task.Delay(33);
                --retryCount;
            }

            SpatialAnchor spatialAnchor = anchor.nativePtr.GetSpatialAnchorFromNativePtr();
            if (spatialAnchor == null)
            {
                throw new InvalidOperationException($"No SpatialAnchor for ARAnchor with position: {anchor.transform.position}, nativePtr: {anchor.nativePtr}, trackingState: {anchor.trackingState}, and pendingState: {anchor.pending}");
            }

            return spatialAnchor;
        }

        /// <summary>
        /// Gets the rotation as a quaternion from a *Unity* 4x4 Pose matrix.
        /// The matrix should be a left-handed matrix, and should operate on column vectors.
        /// </summary>
        /// <param name="matrix">The unity pose matrix</param>
        /// <returns>A quaternion representing the rotation</returns>
        static public Quaternion GetRotation(this UnityEngine.Matrix4x4 matrix)
        {
            return Quaternion.LookRotation(matrix.GetColumn(2), matrix.GetColumn(1));
        }

        /// <summary>
        /// Gets the translation as a vector from a *Unity* 4x4 Pose matrix.
        /// The matrix should be a left-handed matrix, and should operate on column vectors.
        /// </summary>
        /// <param name="matrix">The unity pose matrix</param>
        /// <returns>A vector representing the translation</returns>
        static public Vector3 GetTranslation(this UnityEngine.Matrix4x4 matrix)
        {
            return matrix.GetColumn(3);
        }

        /// <summary>
        /// Effectively LookAt(targetPosition), and then turn around.
        /// Useful when you want text to be readable from a target point.
        /// If I hold up a sign facing in your direction so that I'm looking at the text, you'd be looking
        /// at the back of the sign. If I hold it facing away from you, you would be looking at the text 
        /// over my shoulder, so we'd both see it oriented correctly.
        /// Diagram with explanation here:
        /// https://answers.unity.com/questions/132592/lookat-in-opposite-direction.html
        /// </summary>
        /// <param name="thisTransform">The transform this method is acting on. </param>
        /// <param name="targetPosition">The position of the target to face away from.</param>
        static public void LookAwayFrom(this Transform thisTransform, Vector3 targetPosition)
        {
            thisTransform.rotation = Quaternion.LookRotation(thisTransform.position - targetPosition);
        }

        static public System.Numerics.Matrix4x4 ConvertToRightHandedRowVectorMatrix(UnityEngine.Matrix4x4 leftHandedColumnVectorOperations)
        {
            // To convert handedness, we flip Z (meaning Z column and Z row are negated, and at their intersection, the two negations cancel out).
            // The matrix is also being transposed to convert from column vector to row vector operations.
            System.Numerics.Matrix4x4 rightHandedRowVectorOperations = new System.Numerics.Matrix4x4();
            rightHandedRowVectorOperations.M11 = leftHandedColumnVectorOperations.m00;
            rightHandedRowVectorOperations.M12 = leftHandedColumnVectorOperations.m10;
            rightHandedRowVectorOperations.M13 = -leftHandedColumnVectorOperations.m20;
            rightHandedRowVectorOperations.M14 = leftHandedColumnVectorOperations.m30;
            rightHandedRowVectorOperations.M21 = leftHandedColumnVectorOperations.m01;
            rightHandedRowVectorOperations.M22 = leftHandedColumnVectorOperations.m11;
            rightHandedRowVectorOperations.M23 = -leftHandedColumnVectorOperations.m21;
            rightHandedRowVectorOperations.M24 = leftHandedColumnVectorOperations.m31;
            rightHandedRowVectorOperations.M31 = -leftHandedColumnVectorOperations.m02;
            rightHandedRowVectorOperations.M32 = -leftHandedColumnVectorOperations.m12;
            rightHandedRowVectorOperations.M33 = leftHandedColumnVectorOperations.m22;
            rightHandedRowVectorOperations.M34 = -leftHandedColumnVectorOperations.m32;
            rightHandedRowVectorOperations.M41 = leftHandedColumnVectorOperations.m03;
            rightHandedRowVectorOperations.M42 = leftHandedColumnVectorOperations.m13;
            rightHandedRowVectorOperations.M43 = -leftHandedColumnVectorOperations.m23;
            rightHandedRowVectorOperations.M44 = leftHandedColumnVectorOperations.m33;
            return rightHandedRowVectorOperations;
        }

        static public UnityEngine.Matrix4x4 ConvertToLeftHandedColumnVectorMatrix(System.Numerics.Matrix4x4 rightHandedRowVectorOperations)
        {
            // To convert handedness, we flip Z (meaning Z column and Z row are negated, and at their intersection, the two negations cancel out).
            // The matrix is also being transposed to convert from row vector to column vector operations.
            UnityEngine.Matrix4x4 leftHandedColumnVectorOperations = new UnityEngine.Matrix4x4();
            leftHandedColumnVectorOperations.m00 = rightHandedRowVectorOperations.M11;
            leftHandedColumnVectorOperations.m10 = rightHandedRowVectorOperations.M12;
            leftHandedColumnVectorOperations.m20 = -rightHandedRowVectorOperations.M13;
            leftHandedColumnVectorOperations.m30 = rightHandedRowVectorOperations.M14;
            leftHandedColumnVectorOperations.m01 = rightHandedRowVectorOperations.M21;
            leftHandedColumnVectorOperations.m11 = rightHandedRowVectorOperations.M22;
            leftHandedColumnVectorOperations.m21 = -rightHandedRowVectorOperations.M23;
            leftHandedColumnVectorOperations.m31 = rightHandedRowVectorOperations.M24;
            leftHandedColumnVectorOperations.m02 = -rightHandedRowVectorOperations.M31;
            leftHandedColumnVectorOperations.m12 = -rightHandedRowVectorOperations.M32;
            leftHandedColumnVectorOperations.m22 = rightHandedRowVectorOperations.M33;
            leftHandedColumnVectorOperations.m32 = -rightHandedRowVectorOperations.M34;
            leftHandedColumnVectorOperations.m03 = rightHandedRowVectorOperations.M41;
            leftHandedColumnVectorOperations.m13 = rightHandedRowVectorOperations.M42;
            leftHandedColumnVectorOperations.m23 = -rightHandedRowVectorOperations.M43;
            leftHandedColumnVectorOperations.m33 = rightHandedRowVectorOperations.M44;
            return leftHandedColumnVectorOperations;
        }

        internal static UnityEngine.Matrix4x4 ToMatrix(this UnityEngine.Pose pose)
        {
            return UnityEngine.Matrix4x4.TRS(pose.position, pose.rotation, UnityEngine.Vector3.one);
        }
    }
}