using Microsoft.Azure.SpatialAnchors.Unity;
using System;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace Microsoft.Azure.SpatialAnchors
{
    public class ASAMapChunkComponent : MonoBehaviour
    {
        [Header("Unity Components")]
        public Material VertexColorMaterial = null;

        private const float KeyframeAxisLength = 5e-2f;
        private const float KeyframeAxisThickness = 2.5e-3f;
        private const float FeaturePointRadius = 2e-3f;
        private const int FeaturePointTessellation = 3;

        private static readonly Color s_FeaturePointColor = Color.yellow;

        private ASALocalAnchor _localAnchor = null;
        private bool _isObjectAnchored = false;

        public ASAMapChunkComponent()
        {
            // Does nothing until SetMapChunk is called.
        }

        /// <summary>
        /// Configures the map chunk to visualize.
        /// </summary>
        /// <remarks>
        /// This method may only be called once. Use a new ASAMapChunkComponent if you need to visualize
        /// another map chunk.
        /// </remarks>
        /// <param name="mapChunk">Description of the map chunk to visualize</param>
        public void SetMapChunk(ASAMapChunk mapChunk)
        {
            if (mapChunk == null)
            {
                throw new ArgumentNullException(nameof(mapChunk));
            }

            if (_localAnchor != null)
            {
                throw new ArgumentException("Map chunk already configured");
            }

            _localAnchor = mapChunk.LocalAnchor;
            AddRenderableComponents(mapChunk);
        }

        public void Update()
        {
            if (_localAnchor == null)
            {
                // Not yet initialized
                return;
            }

            // Attempt to anchor the visualization
            if (!_isObjectAnchored)
            {
                Pose? localAnchorToWorldPose = _localAnchor.TryComputeAnchorToWorldPose();
                if (localAnchorToWorldPose == null)
                {
                    return;
                }

                gameObject.transform.SetPositionAndRotation(
                    localAnchorToWorldPose.Value.position,
                    localAnchorToWorldPose.Value.rotation);

                gameObject.AddComponent<ARAnchor>();
                _isObjectAnchored = true;
            }
        }

        private void AddRenderableComponents(ASAMapChunk mapChunk)
        {
            Mesh mesh = CreateMapChunkMesh(mapChunk);

            var meshRenderer = gameObject.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = VertexColorMaterial;
            var meshFilter = gameObject.AddComponent<MeshFilter>();
            meshFilter.mesh = mesh;
        }

        private static Mesh CreateMapChunkMesh(ASAMapChunk mapChunk)
        {
            var builder = new PrimitiveBuilder();

            // Visualize the keyframe poses
            foreach (MapKeyframe mapKeyframe in mapChunk.Keyframes)
            {
                Matrix4x4 keyframeToLocalAnchorPoseMatrix = mapKeyframe.KeyframeToLocalAnchorPose;

                // Decompose the pose matrix
                Vector3 keyframeToLocalAnchorTranslation = keyframeToLocalAnchorPoseMatrix.GetTranslation();
                Quaternion keyframeToLocalAnchorRotation = keyframeToLocalAnchorPoseMatrix.GetRotation();

                var keyframeToLocalAnchorPose = new Pose(keyframeToLocalAnchorTranslation, keyframeToLocalAnchorRotation);
                builder.AddAxis(KeyframeAxisLength, KeyframeAxisThickness, keyframeToLocalAnchorPose);
            }

            // Visualize the reconstructed feature points
            foreach (MapFeature mapFeature in mapChunk.Features)
            {
                Vector3 featurePoint = mapFeature.PositionInLocalAnchorSpace;
                var featurePointPose = new Pose(featurePoint, Quaternion.identity);

                builder.AddSphere(FeaturePointRadius, FeaturePointTessellation, s_FeaturePointColor, featurePointPose);
            }

            return builder.BuildMesh();
        }
    }
}