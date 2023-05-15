using System;
using System.Threading;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

#if ENABLE_WINMD_SUPPORT
using Windows.Perception.Spatial;
#endif

namespace Microsoft.Azure.SpatialAnchors.Unity
{
    /// <summary>
    /// <c>ASAAnchorController</c> is attached as a component to an existing Unity GameObject. It
    /// holds the reference to the ASA SDK's <c>ASAAnchor</c> and attempts to keep the GameObject
    /// fixed within the map.
    /// When the application receives the <c>ASAAnchor</c> back from the SDK (either via creation,
    /// resolving, or discovery) it should immediately attach an <c>ASAAnchorController</c> to the
    /// GameObject associated with the anchor. Then, the application will call <c>ASAAnchor.SetASAAnchor()</c>.
    /// Under the covers, <c>ASAAnchorController</c> will convert the pose of the ASAAnchor into the Unity
    /// coordinate system at the start of every frame and set the GameObject's transform. As the device tracker
    /// learns about the environment and the service refines the local-to-cloud map correspondence, the
    /// <c>ASAAnchor</c> will update and so too will the owning GameObject.
    /// </summary>
    [DefaultExecutionOrder(int.MinValue)]
    public class ASAAnchorController : MonoBehaviour
    {
        /// <summary>
        /// The reference to the underlying ASA SDK anchor. In ASA SDK 3.0, this will become a
        /// custom data type that is handled natively by the SDK. For now, it is a thin wrapper on
        /// top of the existing <c>CloudSpatialAnchor</c>. Every time the application receives an
        /// <c>ASAAnchor</c>, it is responsible for calling <c>ASAAnchorController.SetASAAnchor()</c>
        /// which will replace this reference.
        /// </summary>
        public ASAAnchor Anchor { get; private set; } = null;

        private void Update()
        {
            if (Anchor.Locatability == AnchorLocatabilityStatus.Locatable)
            {
                ApplyLastPoseToWorldOnGameObject();
            }
        }

        /// <summary>
        /// Takes the pose of the incoming <c>ASAAnchor</c> and moves the GameObject there.
        /// The application is responsible for calling this function the first time it is 
        /// handed an <c>ASAAnchor</c>.
        /// Must be called from the main thread.
        /// </summary>
        public void SetASAAnchor(ASAAnchor anchor)
        {
            if (anchor == null) { throw new ArgumentNullException(nameof(anchor)); }

            Anchor = anchor;

            // Move the GameObject to the anchor's current world pose
            // If the anchor has been updated in close proximity, drop an ARAnchor
            ApplyLastPoseToWorldOnGameObject();
        }

        /// <summary>
        /// Immediately move the GameObject to the current world pose of the ASA anchor. Must be called from the main thread.
        /// If the anchor has been updated in close proximity, drop an ARAnchor on the game object.
        /// Must be called on main thread.
        /// </summary>
        private void ApplyLastPoseToWorldOnGameObject()
        {
            Pose newAnchorToWorldPose = Anchor.GetLastAnchorToWorldPose().ToUnityPose();
            transform.SetPositionAndRotation(newAnchorToWorldPose.position, newAnchorToWorldPose.rotation);
        }
    }
}