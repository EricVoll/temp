using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Microsoft.Azure.SpatialAnchors
{
    // Adapted from XrSceneLib / DirectXTK
    internal class PrimitiveBuilder
    {
        private List<Vector3> _vertices = new List<Vector3>();
        private List<Color> _vertexColors = new List<Color>();
        private List<Vector3> _normals = new List<Vector3>();
        private List<int> _indices = new List<int>();

        public Mesh BuildMesh()
        {
            var mesh = new Mesh()
            {
                vertices = _vertices.ToArray(),
                colors = _vertexColors.ToArray(),
                normals = _normals.ToArray(),
                indexFormat = IndexFormat.UInt32,
                triangles = _indices.ToArray(),
            };

            // Upload the mesh to the GPU and discard the CPU buffers.
            mesh.UploadMeshData(markNoLongerReadable: true);

            return mesh;
        }

        public void AddAxis(float axisLength, float axisThickness, Pose partToOriginPose)
        {
            float originThickness = axisThickness + 0.01f;
            AddBox(new Vector3(originThickness, originThickness, originThickness), Color.gray, partToOriginPose);
            Color[] axisColors = new Color[3] { Color.red, Color.green, Color.blue };
            for (int axis = 0; axis < 3; axis++)
            {
                Pose axisToPartPose = Pose.identity;
                axisToPartPose.position[axis] = 0.5f * axisLength;
                Pose axisToOriginPose = axisToPartPose.GetTransformedBy(partToOriginPose);
                Vector3 sideLengths = new Vector3(axisThickness, axisThickness, axisThickness);
                sideLengths[axis] = axisLength;
                AddBox(sideLengths, axisColors[axis], axisToOriginPose);
            }
        }

        public void AddBox(Vector3 sideLengths, Color color, Pose partToOriginPose)
        {
            // A box has six faces, each one pointing in a different direction.
            const int FaceCount = 6;

            // Note: The code below relies on the face ordering to compute a basis.
            Vector3[] faceNormals = new Vector3[FaceCount] {
                Vector3.forward,
                Vector3.back,
                Vector3.right,
                Vector3.left,
                Vector3.up,
                Vector3.down,
            };

            // Create each face in turn.
            Vector3 sideLengthHalfVector = 0.5f * sideLengths;

            for (int i = 0; i < FaceCount; i++)
            {
                Vector3 normal = faceNormals[i];

                // Get two vectors perpendicular both to the face normal and to each other.
                Vector3 basis = (i >= 4) ? Vector3.forward : Vector3.up;

                Vector3 side1 = Vector3.Cross(basis, normal);
                Vector3 side2 = Vector3.Cross(side1, normal);

                int firstVertexIndex = _vertices.Count;

                Vector3[] basePositions = new Vector3[4] {
                    normal - side1 - side2,
                    normal - side1 + side2,
                    normal + side1 + side2,
                    normal + side1 - side2,
                };

                for (int j = 0; j < 4; j++)
                {
                    Vector3 position = Vector3.Scale(basePositions[j], sideLengthHalfVector);
                    AppendVertex(position, color, normal, partToOriginPose);
                }

                // Six indices (two triangles) per face.
                _indices.Add(firstVertexIndex + 0);
                _indices.Add(firstVertexIndex + 1);
                _indices.Add(firstVertexIndex + 2);

                _indices.Add(firstVertexIndex + 0);
                _indices.Add(firstVertexIndex + 2);
                _indices.Add(firstVertexIndex + 3);
            }

        }

        public void AddSphere(float radius, int tessellation, Color color, Pose partToOriginPose)
        {
            if (tessellation < 3)
            {
                throw new ArgumentException("tesselation parameter out of range", nameof(tessellation));
            }

            int verticalSegments = tessellation;
            int horizontalSegments = tessellation * 2;

            int startVertexIndex = _vertices.Count;

            // Create rings of vertices at progressively higher latitudes.
            for (int i = 0; i <= verticalSegments; i++)
            {
                double latitude = (i * Math.PI / verticalSegments) - 0.5f * Math.PI;
                double dy = Math.Sin(latitude);
                double dxz = Math.Cos(latitude);

                // Create a single ring of vertices at this latitude.
                for (int j = 0; j <= horizontalSegments; j++)
                {
                    double longitude = j * 2 * Math.PI / horizontalSegments;
                    double dx = Math.Sin(longitude);
                    double dz = Math.Cos(longitude);

                    var normal = new Vector3((float)(dx * dxz), (float)dy, (float)(dz * dxz));

                    Vector3 position = normal * radius;

                    AppendVertex(position, color, normal, partToOriginPose);
                }
            }

            // Fill the index buffer with triangles joining each pair of latitude rings.
            int stride = horizontalSegments + 1;
            for (int i = 0; i < verticalSegments; i++)
            {
                for (int j = 0; j <= horizontalSegments; j++)
                {
                    int nextI = i + 1;
                    int nextJ = (j + 1) % stride;

                    _indices.Add(startVertexIndex + (i * stride + j));
                    _indices.Add(startVertexIndex + (i * stride + nextJ));
                    _indices.Add(startVertexIndex + (nextI * stride + j));

                    _indices.Add(startVertexIndex + (i * stride + nextJ));
                    _indices.Add(startVertexIndex + (nextI * stride + nextJ));
                    _indices.Add(startVertexIndex + (nextI * stride + j));
                }
            }
        }

        private void AppendVertex(Vector3 position, Color color, Vector3 normal, Pose vertexToOriginPose)
        {
            _vertices.Add(vertexToOriginPose.position + vertexToOriginPose.rotation * position);
            _vertexColors.Add(color);
            _normals.Add(vertexToOriginPose.rotation * normal);
        }
    }
}