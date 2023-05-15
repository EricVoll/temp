using System;
using System.Linq;
using System.Numerics;

namespace Microsoft.Azure.SpatialAnchors
{
    /// <summary>
    /// Set of native extensions. There is a separate set of extensions at the Unity level.
    /// </summary>
    public static class ASAExtensions
    {
        /// <summary>
        /// Convert a Matrix4x4 to a float array.
        /// </summary>
        /// <param name="m">the Matrix4x4</param>
        /// <returns>the float array</returns>
        static public float[] ToArray(this Matrix4x4 m)
        {
            return new float[] {m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 };
        }

        /// <summary>
        /// Convert a float array into a Matrix4x4.
        /// </summary>
        /// <param name="f">the float array</param>
        /// <returns>the Matrix4x4</returns>
        static public Matrix4x4 ToMatrix4x4(this float [] f)
        {
            int matrixLength = 4 * 4; // Hard-coded length of a Matrix4x4
            if (f.Length != matrixLength)
            {
                throw new InvalidOperationException($"Float array is not of size {matrixLength}");
            }

            return new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
        }
    }
}