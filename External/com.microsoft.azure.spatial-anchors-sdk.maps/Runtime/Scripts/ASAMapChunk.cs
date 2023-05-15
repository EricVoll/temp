using System.Collections.Generic;

#if ENABLE_WINMD_SUPPORT
using SpatialAnchor = Windows.Perception.Spatial.SpatialAnchor;
#else
using SpatialAnchor = System.Object;
#endif

namespace Microsoft.Azure.SpatialAnchors
{
    /// <summary>
    /// Describes a portion of the mapped space.
    /// <summary>
    /// <remarks>
    /// An application can visualize the mapped space to guide the user in scanning and provide feedback about the mapping process.
    /// There may be overlap between nearby chunks which could cause visual artifacts due to imperfect alignment.
    /// The data format is experimental and subject to change.
    /// </remarks>
    public class ASAMapChunk
    {
        internal ASAMapChunk(MapChunk mapChunk)
        {
            using var localAnchorHandle = new ComSafeHandle(mapChunk.LocalAnchor);
            var spatialAnchor = (SpatialAnchor)localAnchorHandle.GetRuntimeCallableWrapper();
            LocalAnchor = new ASALocalAnchor(spatialAnchor);
            Keyframes = mapChunk.Keyframes;
            Features = mapChunk.Features;
        }

        /// <summary>
        /// The local anchor defining a root coordinate system for this map chunk.
        /// </summary>
        public ASALocalAnchor LocalAnchor { get; internal set; }

        /// <summary>
        /// The keyframes corresponding to scanned viewpoints which contribute to the map.
        /// </summary>
        public IReadOnlyList<MapKeyframe> Keyframes { get; internal set; }

        /// <summary>
        /// The features used for relocalization.
        /// </summary>
        public IReadOnlyList<MapFeature> Features { get; internal set; }
    }
}