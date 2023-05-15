using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.Profiling;

using Microsoft.Azure.SpatialAnchors.Unity;

#if ENABLE_WINMD_SUPPORT
using Windows.Perception.Spatial;
using SpatialAnchor = Windows.Perception.Spatial.SpatialAnchor;
#else
using SpatialAnchor = System.Object;
#endif

namespace Microsoft.Azure.SpatialAnchors
{
    public class ASASessionConfiguration
    {
#if ASA_USE_PREVIEW_PACKAGE
        /// <summary>
        /// Internal option only exposed for development.
        /// Set to the IP address or hostname of a OneBox ASA deployment.
        /// </summary>
        /// <remarks>
        /// Currently OneBox does not support TLS. Only intended for usage on private networks.
        /// The OneBox host is expected to provide the following services:
        ///  - AFE at insecure HTTP port 8080
        ///  - PQP at insecure gRPC port 8090
        /// AccountDomain is still used to obtain an STS token and should be set to "ppe.mixedreality.azure.com" unless AccessToken is specified.
        /// If your OneBox host is on a private network, the HoloLens app will additionally require the "privateNetworkClientServer" capability.
        /// </remarks>
        public string OneBoxHost = string.Empty;
#endif

        /// <summary>
        /// Relative path to directory to store diagnostics data. Non-empty directory will enable diagnostics
        /// blob generation
        /// </summary>
        public string LogDirectory = string.Empty;
    }

    /// <summary>
    /// Intended to replace <c>CloudSpatialAnchorSession.Configuration</c>. This is passed-in to
    /// <c>ASASession</c> every time Start() is called. Many of these properties will not change
    /// from session to session, so these properties could be separated into separate objects. For
    /// now, the data is passed-in every time the session is started.
    /// </summary>
    public class ASAMapConfiguration
    {
        /// <summary>
        /// The string identifier for the map. This value is currently required. If not set,
        /// <c>ASASession.Start()</c> will throw an exception.
        /// </summary>
        public Guid MapId;

        /// <summary>
        /// Controls whether the <c>ASASession</c> is configured for collecting environment
        /// information as a part of the scanning phase of the curation workflow. If set true,
        /// anchor handling cannot occur.
        /// </summary>
        public bool MappingEnabled = false;

        /// <summary>
        /// Creates a deep copy of the map configuration which won't change when the original object changes.
        /// </summary>
        /// <returns>A deep copy of this</returns>
        internal ASAMapConfiguration DeepCopy()
        {
            return (ASAMapConfiguration)MemberwiseClone();
        }
    }

    /// <summary>
    /// This class encapsulates map meta data that we get as result of the GetMapsAsync function.
    /// It includes up to a certain limit number of entries and may not represent the entire map
    /// collection.
    /// </summary>
    public class ASAMapMetaDataPage
    {
        /// <summary> Dictionary of MapMetaData, including records of map Id and map name pair.</summary>
        public Dictionary<Guid, string> MetaData { get; set; }

        /// <summary>If not empty, indicates there is more map meta data avaible and the value can be used
        /// as parameter to GetMapsAsync to fetch the next batch of map meta data.
        /// Note that, due to possible changes to map data storage until the next call to GetMapsAsync, the
        /// call with continuationToken may pick up those changes and the results may contain duplicate
        /// or missing map meta data entries.</summary>
        public string ContinuationToken { get; set; }
    }

    internal class ASASessionCredentials
    {
        public Guid AccountId;
        public string AccessToken = string.Empty;
        public string AccountKey = string.Empty;
        public string AccountDomain = string.Empty;
    }

    internal class AnchorUpdate
    {
        public ASALocalAnchor LocalAnchor;

        public System.Numerics.Matrix4x4 CloudAnchorToLocalAnchorPose;
    }

    internal class WatchedAnchor
    {
        public ASAAnchor Anchor;

        /// <summary>
        /// After polling for an updated platform anchor in the background, this field buffers
        /// the update to be applied in the next tick.
        /// </summary>
        public AnchorUpdate PendingAnchorUpdate;

        /// <summary>
        /// Flag indicating whether the anchor has already been surfaced to the app via the
        /// AnchorDiscovered event.
        /// </summary>
        public bool SurfacedToApp;
    }

    /// <summary>
    /// The <c>ASASession</c> is intended to extend and abstract away the
    /// <c>CloudSpatialAnchorSession</c>. It is marked an internal now because it is not intended
    /// to be accessed directly by the application. All  app layer interaction should occur through
    /// the <c>ASASessionManager</c>.
    /// </summary>
    internal class ASASession : CloudSpatialAnchorSession
    {
        /// <summary>
        /// The configuration object that is passed to the <c>ASASession</c> via the EnterMap
        /// function. We hold a deep copy of the configuration for reference while the session is
        /// underway. The configuration won't change until the session is stopped and then started
        /// again.
        /// </summary>
        private ASAMapConfiguration _mapConfiguration;

        /// <summary>
        /// This data structure is used to keep track of the anchors that have either been
        /// created or recalled from an existing map. The key is the anchor's identifier. This list
        /// is cleared during map reconnection and session restarts.
        /// </summary>
        private ConcurrentDictionary<Guid, WatchedAnchor> _anchorsWatched = new ConcurrentDictionary<Guid, WatchedAnchor>();

        /// <summary>
        /// Controls if anchor discovery is enabled. If enabled, the session keeps track of the
        /// device pose. If the device comes with the <c>DiscoveryDistanceThreshold</c> of an
        /// anchor that not yet been returned to the application, that anchor is returned to the
        /// application via the <c>_anchorDiscoveredEvent</c>. The DiscoveryDistanceThreshold is
        /// defined in meters.
        /// </summary>
        private bool _discoveryEnabled = false;
        private float _discoveryDistanceThresholdMeters = 5.0f;

        /// <summary>
        /// Property tracks the map query operation progress.
        /// </summary>
        private Task _queryInProgress = Task.CompletedTask;

        /// <summary>
        /// Tracks whether the session has been started. Reset to false when stopped.
        /// </summary>
        private bool _active = false;

        /// <summary>
        /// Tracks whether the session has been paused, suspending background operations (and the update loop)
        /// </summary>
        private bool _paused = false;

        /// <summary>
        /// Measures time elapsed without fetching updated spatial anchors from the SDK via QuerySpatialAnchorsInMapAsync.
        /// </summary>
        private Stopwatch _idleStopwatch = Stopwatch.StartNew();

#if ENABLE_WINMD_SUPPORT
        // Use a class member to ensure consistent references to the same instance of SpatialLocator
        private Windows.Perception.Spatial.SpatialLocator _spatialLocator;
#endif

        public static UnityEngine.Vector3 _lastCameraPositionInWorld = UnityEngine.Vector3.zero;

        /// <summary>
        /// Maximum time elapsed without fetching updated spatial anchors from the SDK via QuerySpatialAnchorsInMapAsync.
        /// </summary>
        private static readonly TimeSpan ForcedRefreshInterval = TimeSpan.FromMilliseconds(250);

        private static readonly ProfilerMarker s_ComputeDistancesMarker = new ProfilerMarker("ASASession.Update.ComputeDistances");
        private static readonly ProfilerMarker s_StartRefinementMarker = new ProfilerMarker("ASASession.Update.StartRefinement");
        private static readonly ProfilerMarker s_ProcessQueryResultsMarker = new ProfilerMarker("ASASession.Update.ProcessQueryResults");

        /// <summary>
        /// We do not want to accidentally call <c>CloudSpatialAnchorSession.Start()</c> directly.
        /// </summary>
        [Obsolete("Please use alternative StartSession() call", true)]
        new internal Task StartAsync(CancellationToken ct = default) { throw new InvalidOperationException(); }

        /// <summary>
        /// Starts the session by calling base.Start().
        /// </summary>
        internal async Task StartSessionAsync(
            ASASessionConfiguration sessionConfiguration,
            ASASessionCredentials sessionCredentials)
        {
            if ((sessionCredentials.AccountId == Guid.Empty) ||
                (sessionCredentials.AccountKey == string.Empty && sessionCredentials.AccessToken == string.Empty) ||
                (sessionCredentials.AccountDomain == string.Empty))
            {
                throw new InvalidOperationException("ASA account credentials are not set");
            }

            LogInfo("Starting new session...");

            Configuration.AccountId = sessionCredentials.AccountId.ToString();
            Configuration.AccountKey = sessionCredentials.AccountKey;
            Configuration.AccessToken = sessionCredentials.AccessToken;
            Configuration.AccountDomain = sessionCredentials.AccountDomain;
#if ASA_USE_PREVIEW_PACKAGE
            Configuration.OneBoxHost = sessionConfiguration.OneBoxHost;
#endif

            if (!string.IsNullOrEmpty(sessionConfiguration.LogDirectory))
            {
                Diagnostics.Directory = sessionConfiguration.LogDirectory;
            }

#if ENABLE_WINMD_SUPPORT
            _spatialLocator = SpatialLocator.GetDefault();
#endif

            // Subscribe to events before starting the session.
            base.LogMessage += OnLogMessage;
            base.Error += OnError;
            base.MapExtended += OnMapExtended;

            await base.StartAsync();
            _active = true;

            LogInfo("New session started!");
        }

        internal new void Pause()
        {
            base.Pause();
            this._paused = true;
        }

        internal new void Resume()
        {
            base.Resume();
            this._paused = false;
        }

        /// <summary>
        /// Sends data to the SDK to aid diagnostics logging by providing content to ground truth detections
        /// </summary>
#pragma warning disable CS1998 // Conditional compile statements are removing await
        internal async Task RegisterGroundTruthMeasurementForContent(string contentId, ASALocalAnchor asaLocalContent)
#pragma warning restore CS1998
        {
            using var localAnchorHandle = new ComSafeHandle(asaLocalContent.SpatialAnchor);
#if ASA_USE_PREVIEW_PACKAGE
            await base.RegisterGroundTruthContentAsync(contentId, localAnchorHandle.DangerousGetHandle());
#else
            throw new NotSupportedException("Cannot invoke diagnostics message with non preview SDK");
#endif
        }

        /// <summary>
        /// Starts a session in a specified map ID by calling base.StartMapSessionAsync.
        /// </summary>
        internal async Task EnterMap(ASAMapConfiguration sessionConfigurationForMap)
        {
            await base.EnterMapAsync(sessionConfigurationForMap.MapId.ToString(), sessionConfigurationForMap.MappingEnabled ? EnterMapMode.Mapping : EnterMapMode.Querying);

            _mapConfiguration = sessionConfigurationForMap.DeepCopy();

            LogInfo($"EnterMap: new session in map started! map id: {_mapConfiguration.MapId}, mapping: {_mapConfiguration.MappingEnabled}");
        }

        /// <summary>
        /// Delete a specified map ID by calling base.DeleteMapAsync.
        /// Note that the case where it's the same map Id whose current session is connected to
        /// is not fully handled - the session may need to be disconnected.
        /// </summary>
        internal async Task DeleteMap(Guid mapId)
        {
            await base.DeleteMapAsync(mapId.ToString());

            LogInfo($"Map deleted! map id: {mapId}");
        }

        /// <summary>
        /// An update loop that is triggered from <c>ASASessionManager</c>'s Update call.
        /// <c>Update</c> is responsible for anchor discovery and pose refinement. It takes the
        /// device's current location and computes the distance to each anchor and then performs
        /// the relevant behavior. It is also responsible for detecting map (re-)connection and
        /// pose refinement timeouts.
        /// </summary>
        internal void Update()
        {
            if (_paused || !_active)
            {
                return;
            }

            _lastCameraPositionInWorld = UnityEngine.Camera.main.transform.position;

#if ENABLE_WINMD_SUPPORT
            if (_spatialLocator == null || _spatialLocator.Locatability != SpatialLocatability.PositionalTrackingActive)
            {
                return;
            }
#endif

            if (_mapConfiguration != null && !_mapConfiguration.MappingEnabled)
            {
                UpdateAnchors();
            }
        }

        /// <summary>
        /// Loops over all watched anchors to process pose updates and discovery notifications.
        /// Notifies anchors which are within discovery radius of the device. The notification
        /// can only happen at most once per anchor.
        /// </summary>
        /// <returns>Returns true if the session should poll for new anchors</returns>
        private bool ProcessAnchorUpdates()
        {
            if (!_discoveryEnabled)
            {
                return false;
            }

            using (s_ComputeDistancesMarker.Auto())
            {
                bool shouldRefreshAnchors = _anchorsWatched.Count == 0 || _idleStopwatch.Elapsed > ForcedRefreshInterval;
                foreach (WatchedAnchor watchedAnchor in _anchorsWatched.Values)
                {
                    // Apply buffered anchor update.
                    if (watchedAnchor.PendingAnchorUpdate != null)
                    {
                        AnchorUpdate anchorUpdate = watchedAnchor.PendingAnchorUpdate;
                        watchedAnchor.Anchor.SetLocalAnchor(anchorUpdate.LocalAnchor, anchorUpdate.CloudAnchorToLocalAnchorPose);
                        watchedAnchor.PendingAnchorUpdate = null;
                    }

                    float distanceSqr = GetDistanceSqrToDevice(watchedAnchor.Anchor);
                    if (distanceSqr == float.MaxValue)
                    {
                        // Currently unlocatable.
                        shouldRefreshAnchors = true;
                        continue;
                    }

                    // Check whether the anchor should be surfaced to the app.
                    if (!watchedAnchor.SurfacedToApp
                        && distanceSqr < _discoveryDistanceThresholdMeters * _discoveryDistanceThresholdMeters)
                    {
                        LogInfo($"Notify discovery for anchor {watchedAnchor.Anchor.Identifier}, distance to device: {Math.Sqrt(distanceSqr)} meters");
                        watchedAnchor.SurfacedToApp = true;
                        ASAAnchorDiscoveredEventArgs anchorDiscoveredEventArgs = new ASAAnchorDiscoveredEventArgs
                        {
                            Anchor = watchedAnchor.Anchor,
                        };
                        _anchorDiscoveredEvent?.Invoke(anchorDiscoveredEventArgs);
                    }
                }
                return shouldRefreshAnchors;
            }
        }

        private void UpdateAnchors()
        {
            using (s_StartRefinementMarker.Auto())
            {
                if (!_queryInProgress.IsCompleted)
                {
                    return;
                }

                _queryInProgress = ProcessAnchorUpdatesAndMaybeScheduleRefresh();
            }
        }

        private async Task ProcessAnchorUpdatesAndMaybeScheduleRefresh()
        {
            try
            {
                bool shouldRefreshAnchors = ProcessAnchorUpdates();
                if (shouldRefreshAnchors)
                {
                    await Task.Run(() => PollForAnchorUpdates());
                }
            }
            catch (Exception ex)
            {
                LogError($"Exception during MaybePerformQueryAndNotifyDiscoveries(): \n {ex}");
            }
        }

        /// <summary>
        /// We do not want to accidentally call the <c>CloudSpatialAnchorSession.Stop()</c> method.
        /// </summary>
        [Obsolete("Please use alternative StopSessionAsync() call", true)]
        new internal Task StopAsync(CancellationToken ct = default) { throw new InvalidOperationException(); }

        /// <summary>
        /// Stops the session by calling base.Stop().
        /// </summary>
        internal async Task StopSessionAsync()
        {
            LogInfo("Stopping the session...");

            // Stop further processing in the Unity layer
            _discoveryEnabled = false;
            _active = false;

            // Stop the native SDK session
            await base.StopAsync();

            // Wait for stopped background work to exit
            if (!_queryInProgress.IsCompleted)
            {
                await _queryInProgress.ContinueWith(_ => { });
            }

            // Clear the Unity layer
            _anchorsWatched.Clear();

            LogInfo("Session stopped");
        }

        /// <summary>
        /// Create a map in the service.
        /// </summary>
        /// <param name="mapName">The plan text map name</param>
        /// <returns>the service-created unique map identifier</returns>
        internal async Task<Guid> CreateMap(string mapName)
        {
            Guid mapId = new Guid(await base.CreateMapAsync(mapName));
            return mapId;
        }

        /// <summary>
        /// Retrieve the list of maps from the service.
        /// </summary>
        /// <returns>a dictionary of the maps with map identifier as the key and map name as the value and
        /// a continuation token that can be used to fetch the next set of entries if available.</returns>
        internal async Task<ASAMapMetaDataPage> EnumerateMapNames(string continuationToken)
        {
            ASAMapMetaDataPage mapsMetaData = new ASAMapMetaDataPage
            {
                MetaData = new Dictionary<Guid, string>()
            };

            MapMetaDataPage mapMetaDataResults;
            try
            {
                mapMetaDataResults = await base.GetMapsAsync(continuationToken);
            }
            catch (CloudSpatialException ex)
            {
                _sessionErrorEvent?.Invoke(new ASASessionErrorEventArgs()
                {
                    ErrorMessage = $"EnumerateMapNames: enumerate maps operation failed. Error: " + ex.Message,
                    ErrorCode = ex.ErrorCode
                });
                return null;
            }

            foreach (MapMetaData metaData in mapMetaDataResults.MetaData)
            {
                Guid mapId = new Guid(metaData.MapId);
                mapsMetaData.MetaData[mapId] = metaData.MapName;
            }
            mapsMetaData.ContinuationToken = mapMetaDataResults.ContinuationToken;
            return mapsMetaData;
        }

        /// <summary>
        /// Gets an <c>ASAAnchor</c> from the the service for the persistence of a piece of content.
        /// </summary>
        /// <param name="contentSpatialAnchor">a local anchor indicating where the user wants to persist the content</param>
        /// <returns>The anchor, and the pose from the desired content location to it</returns>
        internal async Task<Tuple<ASAAnchor, UnityEngine.Matrix4x4>> GetSpatialAnchor(SpatialAnchor contentSpatialAnchor)
        {
            if (contentSpatialAnchor == null)
            {
                throw new NullReferenceException("Local SpatialAnchor for content is null");
            }

            if (_mapConfiguration.MappingEnabled)
            {
                throw new InvalidOperationException("Cannot place content while mapping is enabled");
            }

            LogInfo("Getting anchor for content placement...");

            using var localAnchorHandle = new ComSafeHandle(contentSpatialAnchor);
            CloudSpatialAnchor getSpatialAnchorInMapRequestAndResponse = new CloudSpatialAnchor();
            getSpatialAnchorInMapRequestAndResponse.LocalAnchor = localAnchorHandle.DangerousGetHandle(); // Safe because it is only used within the ComSafeHandle's scope.

            // When entering GetSpatialAnchorInMapAsync, the parameter contains
            //      id: not set
            //      platformAnchor: a platform anchor that represents the desired content location.
            //      platformAnchorToSpatialAnchorRowMajorPoseMatrix: not set.
            try
            {
                await base.GetSpatialAnchorInMapAsync(getSpatialAnchorInMapRequestAndResponse);
            }
            catch (CloudSpatialException ex)
            {
                string errorMessage = $"GetSpatialAnchor: operation failed. Error: " + ex.Message;
                _sessionErrorEvent?.Invoke(new ASASessionErrorEventArgs()
                {
                    ErrorMessage = errorMessage,
                    ErrorCode = ex.ErrorCode
                });
                throw new InvalidOperationException(errorMessage);
            }
            // When exiting GetSpatialAnchorInMapAsync, it should contain
            //      id: the ID of the ASA Spatial Anchor that should be used to attach the content.
            //      platformAnchor: a platform anchor that corresponds the returned Spatial Anchor above.
            //      platformAnchorToSpatialAnchorRowMajorPoseMatrix: The pose from the content to the identified spatial anchor.

            // The matrix in the SDK is RIGHT-handed (X x Y = Z)
            // and operates on COLUMN vectors:
            // [R t]
            // [0 1]

            // The ApiGen layer flips the Z-axis... so this matrix is LEFT-handed (X x Y = -Z)
            // and operates on COLUMN vectors:
            // [R t]
            // [0 1]

            // The returned matrix follows the Unity convention and can be used as-is.
            // We do not use this offset matrix because the state of ASA anchors in Unity shim layer is different from that in
            // the native SDK. The shim layer updates anchors by polling every ForcedRefreshInterval and may lag behind the native SDK.
            // Hence we re-compute the pose offset to the returned spatial anchor to account for the outdated state in Unity shim.
            // UnityEngine.Matrix4x4 contentToReturnedSpatialAnchorPose =
            //    getSpatialAnchorInMapRequestAndResponse.PlatformAnchorToSpatialAnchorPose;

            Guid returnedAnchorId = Guid.Parse(getSpatialAnchorInMapRequestAndResponse.Identifier);
            if (!_anchorsWatched.TryGetValue(
                new Guid(getSpatialAnchorInMapRequestAndResponse.Identifier),
                out WatchedAnchor watchedAnchor))
            {
                ASASessionErrorEventArgs eventArgs = new ASASessionErrorEventArgs()
                {
                    ErrorCode = CloudSpatialErrorCode.Unknown,
                    ErrorMessage = $"Unable to track anchor {returnedAnchorId} returned as a result of GetAnchor."
                };
                _sessionErrorEvent?.Invoke(eventArgs);

                throw new InvalidOperationException($"Unable to track anchor {returnedAnchorId} returned as a result of GetAnchor.");
            }

            ASAAnchor existingAsaAnchor = watchedAnchor.Anchor;
            LogInfo($"GetAnchor() returned an already watched anchor {existingAsaAnchor.Identifier}.");

            // Recompute the pose offset from the content to this anchor
            System.Numerics.Matrix4x4? contentToReturnedSpatialAnchorPose = null;
            System.Numerics.Matrix4x4? contentToWorldPoseViaPlacement = null;
            System.Numerics.Matrix4x4 anchorToWorldPose = existingAsaAnchor.GetLastAnchorToWorldPose();
#if ENABLE_WINMD_SUPPORT
            SpatialAnchor pivotAnchor = existingAsaAnchor.LocalAnchor.SpatialAnchor;
            System.Numerics.Matrix4x4? contentToPivotAnchorPose = contentSpatialAnchor.CoordinateSystem.TryGetTransformTo(pivotAnchor.CoordinateSystem);
            if (contentToPivotAnchorPose.HasValue &&
                System.Numerics.Matrix4x4.Invert(
                    existingAsaAnchor.CloudAnchorToLocalAnchorPose,
                    out System.Numerics.Matrix4x4 pivotAnchorToSpatialAnchorPose))
            {
                contentToReturnedSpatialAnchorPose = contentToPivotAnchorPose * pivotAnchorToSpatialAnchorPose;
            }
            contentToWorldPoseViaPlacement = WinMRInterop.TryComputeAnchorToWorldPose(contentSpatialAnchor);
#endif

            // Logging to debug content jump while getting anchor for content
            if (contentToWorldPoseViaPlacement.HasValue && contentToReturnedSpatialAnchorPose.HasValue)
            {
                System.Numerics.Matrix4x4? contentToWorldPoseViaAnchor = contentToReturnedSpatialAnchorPose * anchorToWorldPose;
                UnityEngine.Matrix4x4 anchorToWorldInUnity =
                    ASAUnityExtensions.ConvertToLeftHandedColumnVectorMatrix(anchorToWorldPose);
                UnityEngine.Matrix4x4 contentToAnchorInUnity =
                    ASAUnityExtensions.ConvertToLeftHandedColumnVectorMatrix(contentToReturnedSpatialAnchorPose.Value);
                UnityEngine.Matrix4x4 contentToWorldPoseViaAnchorInUnity =
                    ASAUnityExtensions.ConvertToLeftHandedColumnVectorMatrix(contentToWorldPoseViaAnchor.Value);
                UnityEngine.Matrix4x4 contentToWorldPoseViaPlacementInUnity =
                    ASAUnityExtensions.ConvertToLeftHandedColumnVectorMatrix(contentToWorldPoseViaPlacement.Value);
                UnityEngine.Vector3 contentInWorldViaAnchor = contentToWorldPoseViaAnchorInUnity.GetTranslation();
                UnityEngine.Vector3 contentInWorldViaPlacement = contentToWorldPoseViaPlacementInUnity.GetTranslation();
                float shiftInMeters = (contentInWorldViaAnchor - contentInWorldViaPlacement).magnitude;
                LogDebug($"anchorToWorld: {anchorToWorldInUnity.GetRotation():F6}, {anchorToWorldInUnity.GetTranslation():F6}");
                LogDebug($"contentToAnchor: {contentToAnchorInUnity.GetRotation():F6}, {contentToAnchorInUnity.GetTranslation():F6}");
                LogDebug($"contentToWorldViaAnchor: {contentToWorldPoseViaAnchorInUnity.GetRotation():F6}, {contentToWorldPoseViaAnchorInUnity.GetTranslation():F6}");
                LogDebug($"contentToWorldViaPlacement: {contentToWorldPoseViaPlacementInUnity.GetRotation():F6}, {contentToWorldPoseViaPlacementInUnity.GetTranslation():F6}");
                LogDebug($"Shift between content via placement and via anchor: {shiftInMeters} meters");
            }
            else
            {
                ASASessionErrorEventArgs eventArgs = new ASASessionErrorEventArgs()
                {
                    ErrorCode = CloudSpatialErrorCode.Unknown,
                    ErrorMessage = $"Cannot compute poses in ASASession for debugging content shift while saving."
                };
                _sessionErrorEvent?.Invoke(eventArgs);
            }


            if (!contentToReturnedSpatialAnchorPose.HasValue)
            {
                ASASessionErrorEventArgs eventArgs = new ASASessionErrorEventArgs()
                {
                    ErrorCode = CloudSpatialErrorCode.Unknown,
                    ErrorMessage = $"Cannot compute the pose between platform anchors for content and spatial anchor."
                };
                _sessionErrorEvent?.Invoke(eventArgs);

                throw new InvalidOperationException($"Cannot compute the pose between platform anchors for content and spatial anchor.");
            }

            UnityEngine.Matrix4x4 contentToReturnedSpatialAnchorPoseInUnityConvention =
                ASAUnityExtensions.ConvertToLeftHandedColumnVectorMatrix(
                    contentToReturnedSpatialAnchorPose.Value);

            return Tuple.Create(existingAsaAnchor, contentToReturnedSpatialAnchorPoseInUnityConvention);
        }

        /// <summary>
        /// Returns the <c>ASAAnchor</c> from the service given the anchor identifier. Since we
        /// only support maps at the moment we should already have the <c>ASAAnchor</c> in 
        /// <c>AnchorsWatched</c> and can return that directly without requerying. The 
        /// <c>ASAAnchor</c> must belong to the map and must not already be passed back to the app
        /// via creation, resolve, or discovery.
        /// </summary>
        /// <param name="anchorId">The anchor identifier</param>
        /// <returns>the anchor returned from the service</returns>
        internal async Task<ASAAnchor> ResolveAnchor(Guid anchorId)
        {
            // If this anchor is already known to the plugin layer, return it immediately.
            if (_anchorsWatched.TryGetValue(anchorId, out WatchedAnchor watchedAnchor))
            {
                return watchedAnchor.Anchor;
            }

            // Otherwise, we need to check with the SDK that it exists
            AnchorLocateCriteria singleAnchorLocateCriteria = new AnchorLocateCriteria();
            singleAnchorLocateCriteria.Identifiers = new[] { anchorId.ToString() };

            QuerySpatialAnchorsInMapResult queryAnchorsInMapResult;
            try
            {
                queryAnchorsInMapResult = await base.QuerySpatialAnchorsInMapAsync(singleAnchorLocateCriteria);
            }
            catch (KeyNotFoundException)
            {
                // The anchor does not exist in the map
                throw new InvalidOperationException("Anchor must belong to the map");
            }
            catch (CloudSpatialException ex)
            {
                _sessionErrorEvent?.Invoke(new ASASessionErrorEventArgs()
                {
                    ErrorMessage = $"QuerySpatialAnchorsInMapAsync: query Spatial Anchors operation failed. Error: " + ex.Message,
                    ErrorCode = ex.ErrorCode
                });

                throw;
            }

            ASAAnchor asaAnchor;
            ASALocalAnchor localAnchor = null;
            System.Numerics.Matrix4x4 cloudAnchorToLocalAnchorPose = System.Numerics.Matrix4x4.Identity;
            string anchorIdString = anchorId.ToString();

            if (queryAnchorsInMapResult.LocatedAnchors.Any())
            {
                // The anchor exists in the map, and is locatable.
                CloudSpatialAnchor resolvedAnchor = queryAnchorsInMapResult.LocatedAnchors
                    .Single(result => result.Identifier == anchorIdString);

                SpatialAnchor spatialAnchor = resolvedAnchor.GetLocalAnchorAsSpatialAnchorObject();
                if (spatialAnchor != null)
                {
                    localAnchor = new ASALocalAnchor(spatialAnchor);
                    cloudAnchorToLocalAnchorPose =
                        ASAUnityExtensions.ConvertToRightHandedRowVectorMatrix(
                            resolvedAnchor.PlatformAnchorToSpatialAnchorPose.inverse);
                }
            }
            else
            {
                // The anchor exists in the map, but is not locatable
            }

#if ENABLE_WINMD_SUPPORT
            asaAnchor = new ASAAnchor(anchorIdString, localAnchor, cloudAnchorToLocalAnchorPose, _spatialLocator);
#else
            asaAnchor = new ASAAnchor(anchorIdString, localAnchor, cloudAnchorToLocalAnchorPose);
#endif

            var anchorUpdate = new AnchorUpdate
            {
                LocalAnchor = localAnchor,
                CloudAnchorToLocalAnchorPose = cloudAnchorToLocalAnchorPose,
            };

            // Using GetOrAdd, because a parallel call might have added it in the meantime.
            watchedAnchor = _anchorsWatched.GetOrAdd(asaAnchor.Identifier,
                _ => new WatchedAnchor
                {
                    Anchor = asaAnchor,
                    PendingAnchorUpdate = anchorUpdate,
                });

            return watchedAnchor.Anchor;
        }

        private async Task PollForAnchorUpdates()
        {
            // We catch these exceptions and log them because this function is not awaited on and the exceptions would be lost.
            // For other call to base methods, we let the exception get thrown as those operations are awaited by the caller.
            QuerySpatialAnchorsInMapResult queryAnchorsInMapResult;
            try
            {
                AnchorLocateCriteria emptyLocateCriteria = new AnchorLocateCriteria();
                queryAnchorsInMapResult = await base.QuerySpatialAnchorsInMapAsync(emptyLocateCriteria);
                _idleStopwatch.Restart();
            }
            catch (CloudSpatialException ex)
            {
                _sessionErrorEvent?.Invoke(new ASASessionErrorEventArgs()
                {
                    ErrorMessage = $"UpdateLocalAnchorsAndNotify: Query Spatial Anchors operation failed. Error: " + ex.Message,
                    ErrorCode = ex.ErrorCode
                });
                return;
            }

            using (s_ProcessQueryResultsMarker.Auto())
            {
                if (!queryAnchorsInMapResult.LocatedAnchors.Any())
                {
                    LogInfo("Query succeeded but got zero located anchors back.");
                    return;
                }

                foreach (var locatedAnchorResult in queryAnchorsInMapResult.LocatedAnchors)
                {
                    string anchorIdString = locatedAnchorResult.Identifier;
                    Guid anchorId = new Guid(anchorIdString);

                    SpatialAnchor spatialAnchor = locatedAnchorResult.GetLocalAnchorAsSpatialAnchorObject();
                    if (spatialAnchor == null)
                    {
                        LogInfo("Received null plaform anchor for queried anchor.");
                        continue;
                    }

                    System.Numerics.Matrix4x4 cloudAnchorToLocalAnchorPose =
                                ASAUnityExtensions.ConvertToRightHandedRowVectorMatrix(
                                    locatedAnchorResult.PlatformAnchorToSpatialAnchorPose.inverse);

                    ASALocalAnchor localAnchor = new ASALocalAnchor(spatialAnchor);
                    var anchorUpdate = new AnchorUpdate
                    {
                        LocalAnchor = localAnchor,
                        CloudAnchorToLocalAnchorPose = cloudAnchorToLocalAnchorPose,
                    };

                    WatchedAnchor watchedAnchor = _anchorsWatched.GetOrAdd(anchorId, _ =>
                    {
#if ENABLE_WINMD_SUPPORT
                        ASAAnchor asaAnchor = new ASAAnchor(anchorIdString, localAnchor, cloudAnchorToLocalAnchorPose, _spatialLocator);
#else
                        ASAAnchor asaAnchor = new ASAAnchor(anchorIdString, localAnchor, cloudAnchorToLocalAnchorPose);
#endif
                        return new WatchedAnchor { Anchor = asaAnchor };
                    });

                    watchedAnchor.PendingAnchorUpdate = anchorUpdate;
                }
            }
        }

        private float GetDistanceSqrToDevice(ASAAnchor anchor)
        {
            if (anchor.Locatability != AnchorLocatabilityStatus.Locatable)
            {
                return float.MaxValue;
            }
            var anchorPositionInWorld = anchor.GetLastAnchorToWorldPose().ToUnityPose().position;
            var anchorToDeviceTranslationInWorld = anchorPositionInWorld - _lastCameraPositionInWorld;
            return anchorToDeviceTranslationInWorld.sqrMagnitude;
        }

        /// <summary>
        /// Controls whether anchor discovery is enabled.
        /// </summary>
        /// <param name="enabled"></param>
        /// <param name="distanceThreshold">Distance between anchor and device, in meters</param>
        internal void SetDiscovery(bool enabled, float distanceThresholdMeters)
        {
            LogInfo($"Setting discovery to {enabled} with a threshold of {distanceThresholdMeters} meters");

            _discoveryDistanceThresholdMeters = distanceThresholdMeters;
            _discoveryEnabled = enabled;
        }

        private void LogInfo(string message)
        {
            EmitLogEvent(SessionLogLevel.Information, message);
        }

        private void LogWarn(string message)
        {
            EmitLogEvent(SessionLogLevel.Warning, message);
        }

        private void LogError(string message)
        {
            EmitLogEvent(SessionLogLevel.Error, message);
        }

        private void LogDebug(string message)
        {
            EmitLogEvent(SessionLogLevel.Debug, message);
        }

        private void EmitLogEvent(SessionLogLevel logLevel, string message)
        {
            if (logLevel <= LogLevel)
            {
                _sessionLogEvent?.Invoke(new ASASessionLogEventArgs(logLevel, message));
            }
        }

        /// <summary>
        /// Forwards the SDK-level LogMessage event to the Unity shim's event.
        /// </summary>
        /// <param name="args">The event payload</param>
        private void OnLogMessage(SessionLogMessageEventArgs args)
        {
            // No need to check the log level, already pre-filtered in the SDK.
            _sessionLogEvent?.Invoke(new ASASessionLogEventArgs(args.LogLevel, args.Message));
        }

        /// <summary>
        /// Forwards the SDK-level Error event to the Unity shim's event.
        /// </summary>
        /// <param name="args">The event payload</param>
        private void OnError(SessionErrorEventArgs args)
        {
            _sessionErrorEvent?.Invoke(
                new ASASessionErrorEventArgs
                {
                    ErrorCode = args.ErrorCode,
                    ErrorMessage = args.ErrorMessage
                });
        }

        /// <summary>
        /// Forwards the SDK-level MapExtended event to the Unity shim's event,
        /// wrapping the bare Mirage anchor in ASALocalAnchor.
        /// </summary>
        /// <param name="mapChunk">The just-added map chunk</param>
        private void OnMapExtended(MapChunk mapChunk)
        {
            _mapExtendedEvent?.Invoke(new ASAMapExtendedEventArgs(mapChunk));
        }

        /// <summary>
        /// An event that notifies the application of anchor discovery. <c>DiscoveryEnabled</c>
        /// must be set to true and the application must be connected to the map. Hands back a
        /// reference to the discovered <c>ASAAnchor</c>.
        /// </summary>
        internal event ASAAnchorDiscoveredDelegate _anchorDiscoveredEvent;

        /// <summary>
        /// An event that notifies the application that an error has occurred either in the plugin
        /// or in the ASA SDK. Most errors are handled via exception handling but some errors, like
        /// those in event callbacks, are propagated to the app via this event.
        /// </summary>
        internal event ASASessionErrorDelegate _sessionErrorEvent;

        /// <summary>
        /// An event that notifies the application that the plugin or the ASA SDK has a log message
        /// to share.
        /// </summary>
        internal event ASASessionLogDelegate _sessionLogEvent;

        /// <summary>
        /// Occurs when new mapping data has been uploaded.
        /// </summary>
        internal event ASAMapExtendedDelegate _mapExtendedEvent;
    }

    public delegate void ASAAnchorDiscoveredDelegate(ASAAnchorDiscoveredEventArgs args);
    public delegate void ASASessionErrorDelegate(ASASessionErrorEventArgs args);
    public delegate void ASASessionLogDelegate(ASASessionLogEventArgs args);
    public delegate void ASAMapExtendedDelegate(ASAMapExtendedEventArgs args);

    public enum AnchorLocatabilityStatus
    {
        NotLocatable, // tracking is not active on HL device
        Locatable // both tracking on HL device is active and ASA map is re-localized.
        //LocallyTrackedButCloudStale, // Intentionally hidden from applications unless it becomes required to expose.
    }

    /// <summary>
    /// Event arguments for <c>ASASession._anchorDiscoveredEvent</c>. Contains the anchor that was
    /// discovered and a status representing the success of the discovery operation.
    /// </summary>
    public class ASAAnchorDiscoveredEventArgs : EventArgs
    {
        /// <summary>
        /// The discovered anchor.
        /// </summary>
        public ASAAnchor Anchor { get; internal set; } = null;
    }

    /// <summary>
    /// Event arguments for <c>ASASession._sessionErrorEvent</c>. Contains the error code and the 
    /// error message. Used to propagate errors from the ASA SDK or the <c>ASASession</c>.
    /// </summary>
    public class ASASessionErrorEventArgs : EventArgs
    {
        /// <summary>
        /// Identifies the source of an error in the ASA SDK or the <c>ASASession</c>.
        /// </summary>
        public CloudSpatialErrorCode ErrorCode { get; internal set; }

        /// <summary>
        /// The error message.
        /// </summary>
        public string ErrorMessage { get; internal set; }
    }

    /// <summary>
    /// Event arguments for <c>ASASession._sessionLogEvent</c>.
    /// </summary>
    public class ASASessionLogEventArgs : EventArgs
    {
        internal ASASessionLogEventArgs(
            SessionLogLevel logLevel,
            string message)
        {
            LogLevel = logLevel;
            Message = message;
        }

        internal ASASessionLogEventArgs(string message)
            : this(SessionLogLevel.Information, message)
        {
        }

        /// <summary>
        /// Severity of the message.
        /// </summary>
        public SessionLogLevel LogLevel { get; internal set; }

        /// <summary>
        /// Human-readable description of what happened.
        /// </summary>
        public string Message { get; internal set; }
    }

    /// <summary>
    /// Event arguments for <c>ASASession._mapExtendedEvent</c>. Contains a description of the
    /// new map chunk rooted at a local anchor for visualization.
    /// </summary>
    public class ASAMapExtendedEventArgs : EventArgs
    {
        internal ASAMapExtendedEventArgs(MapChunk mapChunk)
        {
            MapChunk = new ASAMapChunk(mapChunk);
        }

        public ASAMapChunk MapChunk { get; internal set; }
    }
}