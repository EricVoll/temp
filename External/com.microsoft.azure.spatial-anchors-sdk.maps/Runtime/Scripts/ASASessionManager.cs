using System;
using System.Threading;
using System.Threading.Tasks;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

#if ENABLE_WINMD_SUPPORT
using Windows.Perception.Spatial;
using SpatialAnchor = Windows.Perception.Spatial.SpatialAnchor;
#else
using SpatialAnchor = System.Object;
#endif

namespace Microsoft.Azure.SpatialAnchors.Unity
{
    /// <summary>
    /// The main object through which an Unity application interacts with the ASA Maps service.
    /// The application will create a single instance of the <c>ASASessionManager</c> that will 
    /// be used over the lifetime of the application run. The <c>ASASessionManager</c> is intended
    /// to provide exposure to the underlying <c>ASASession</c> while keeping the anchors synced
    /// with Unity's ARFoundation. A typical workflow for an application would be to instantiate
    /// the <c>ASASessionManager</c>, call <c>ASASessionManager.StartSession()</c>, perform
    /// some anchor-related operations, then end the session with a call to
    /// <c>ASASessionManager.StopSession()</c>. The application can then change the 
    /// <c>ASASessionConfiguration</c> and start a new session with the same <c>ASASessionManager</c>.
    /// </summary>
    public class ASASessionManager : MonoBehaviour
    {
        private string ASAAccountId = "";
        private string ASAAccountKey = "";
        private string ASAAccessToken = "";
        private string ASAAccountDomain = "";

#if ASA_USE_PREVIEW_PACKAGE
        [SerializeField]
        [Tooltip(
            "Internal option only exposed for development. Set to the IP address or hostname of a OneBox ASA deployment.\n\n" +
            "Currently OneBox does not support TLS. Only intended for usage on private networks.\n" +
            "The OneBox host is expected to provide the following services:\n" +
            " - AFE at insecure HTTP port 8080\n" +
            " - PQP at insecure gRPC port 8090\n" +
            "AccountDomain is still used to obtain an STS token and should be set to \"ppe.mixedreality.azure.com\" unless AccessToken is specified.\n" +
            "If your OneBox host is on a private network, the HoloLens app will additionally require the \"privateNetworkClientServer\" capability.")]
        private string OneBoxHost = "";
#endif

        [Header("Settings")]
        [SerializeField]
        [Tooltip("Automatically suspend the SDK while the application is paused. This setting is on by default to conserve CPU and I/O resources when the application is running in the background.")]
        private bool SuspendInBackground = true;

        [SerializeField]
        [InspectorName("Log Level")]
        [Tooltip("The maximum log level at which session log events are emitted.")]
        private SessionLogLevel _logLevel = SessionLogLevel.Information;

        public SessionLogLevel LogLevel
        {
            get { return _logLevel; }
            set
            {
                _logLevel = value;
                if (_session != null)
                {
                    _session.LogLevel = _logLevel;
                }
            }
        }

        /// <summary>
        /// The native class used to create, locate, and manage spatial anchors. In ASA SDK 3.0,
        /// this will become a fully native object that is integrated in to the platform-specific
        /// packages. For now, it is a wrapper on top of the existing <c>CloudSpatialAnchorSession</c>
        /// that exposes functionality that is similar to that of the eventual ASA Maps API. The
        /// <c>ASASession</c> is instantiated once and then reconfigured and re-used for each new
        /// session.
        /// </summary>
        private ASASession _session = null;

        private static readonly ProfilerMarker s_SessionUpdateMarker = new ProfilerMarker("ASASessionManager.Update.SessionUpdate");
        
        /// <summary>
        /// The standard MonoBehaviour method that is called on the frame when the script is 
        /// enabled just before any of the Update() methods are called for the first time. Checks
        /// to make sure the required objects are in the Unity scene.
        /// </summary>
        void Start()
        {
            // Find the camera in the scene
            if (Camera.main == null)
            {
                Debug.LogError("Must have a camera in the scene with the MainCamera tag");
                return;
            }

            // Find the ARAnchorManager in the scene
            ARAnchorManager arAnchorManager = FindObjectOfType<ARAnchorManager>();
            if (arAnchorManager == null || !arAnchorManager.enabled)
            {
                Debug.LogError("Must have an enabled ARAnchorManager in the scene");
                return;
            }
        }

        /// <summary>
        /// The standard MonoBehaviour method that is called every frame. We use this update loop
        /// to call the underlying <c>ASASession</c>'s Update() method. We pace the loop using 
        /// s_updateFramesPerSecond.
        /// </summary>
        void Update()
        {
            if (_session != null)
            {
                try
                {
                    using(s_SessionUpdateMarker.Auto())
                    {
                        _session.Update();
                    }
                }
                catch (Exception ex)
                {
                    ASASessionErrorEventArgs eventArgs = new ASASessionErrorEventArgs();
                    eventArgs.ErrorCode = CloudSpatialErrorCode.Unknown;
                    eventArgs.ErrorMessage = ex.Message;
                    SessionError?.Invoke(eventArgs);
                }
            }
        }

        /// <summary>
        /// The standard MonoBehaviour method that is called when a scene or game ends.
        /// </summary>
        async void OnDestroy()
        {
            if (_session != null)
            {
                try
                {
                    await StopSessionAsync();
                }
                catch (Exception ex)
                {
                    ASASessionErrorEventArgs eventArgs = new ASASessionErrorEventArgs();
                    eventArgs.ErrorCode = CloudSpatialErrorCode.Unknown;
                    eventArgs.ErrorMessage = ex.Message;
                    SessionError?.Invoke(eventArgs);
                }
            }
        }

        /// <summary>
        /// The standard MonoBehaviour method that is called when the application is moved between background and foreground.
        /// </summary>
        void OnApplicationFocus(bool hasFocus)
        {
            if (!SuspendInBackground)
            {
                return;
            }

            if (hasFocus)
            {
                Resume();
            }
            else
            {
                Pause();
            }
        }

        /// <summary>
        /// The standard MonoBehaviour method that is called when the application is paused or resumed.
        /// </summary>
        void OnApplicationPause(bool isPausing)
        {
            if (!SuspendInBackground)
            {
                return;
            }

            if (isPausing)
            {
                Pause();
            }
            else
            {
                Resume();
            }
        }

        /// <summary>
        /// Checks to make sure that the ASASessionManager has been configured with credentials.
        /// Does not actually verify that the credentials are valid with the service.
        /// </summary>
        private bool AreASACredentialsProvided()
        {
            // Check for existence of ASA resource credentials
            if (string.IsNullOrWhiteSpace(ASAAccountId) ||
                KeyAndAccessTokenAreMissing() ||
                string.IsNullOrWhiteSpace(ASAAccountDomain))
            {
                Debug.LogError("Proper account credentials have not yet been provided");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Checks to make sure that either the account key or the account token have been provided.
        /// </summary>
        private bool KeyAndAccessTokenAreMissing()
        {
            return string.IsNullOrWhiteSpace(ASAAccountKey) && string.IsNullOrWhiteSpace(ASAAccessToken);
        }

        /// <summary>
        /// Allows the caller to configure credentials outside of the Unity inspector. Caller needs
        /// to supply either the account key or the access token.
        /// </summary>
        public bool SetASACredentials(string asaAccountId, string asaAccountDomain, string asaAccountKey = "", string asaAccessToken = "")
        {
            ASAAccountId = asaAccountId;
            ASAAccountDomain = asaAccountDomain;
            ASAAccountKey = asaAccountKey;
            ASAAccessToken = asaAccessToken;

            return AreASACredentialsProvided();
        }


#if ASA_USE_PREVIEW_PACKAGE
        public bool SetASAOneBox(string oneBoxHost)
        {
            OneBoxHost = oneBoxHost;

            return AreASACredentialsProvided();
        }
#endif

        public virtual bool IsSessionStarted()
        {
            return (_session != null);
        }

        /// <summary>
        /// Method for configuring and starting the underlying <c>ASASession</c>. Creates the
        /// <c>ASASession</c>, subscribes to the required events, and starts the session.
        /// </summary>
        /// <param name="configuration">Configures the ASASession until StopSession() is
        /// called</param>
        public virtual async Task StartSessionAsync(ASASessionConfiguration configuration)
        {
            if (!AreASACredentialsProvided()) throw new InvalidOperationException("ASA Account credentials must be provided in the Unity inspector or SetASACredentials()");

#if ENABLE_WINMD_SUPPORT
            // Check for Spatial Perception capability
            SpatialPerceptionAccessStatus spatialPerceptionAccessStatus = await SpatialAnchorExporter.RequestAccessAsync();
            if (spatialPerceptionAccessStatus != SpatialPerceptionAccessStatus.Allowed)
            {
                throw new InvalidOperationException("Spatial Perception capability must be enabled");
            }
#endif

            // If session was already running, go ahead and stop it
            if (IsSessionStarted())
            {
                await StopSessionAsync();
            }

            // Create a new session
            _session = new ASASession();

            // Subscribe to session events
            _session._sessionErrorEvent += OnSessionError;
            _session._sessionLogEvent += OnSessionLog;
            _session._anchorDiscoveredEvent += OnAnchorDiscovered;
            _session._mapExtendedEvent += OnMapExtended;

            // Add the credentials from the inspector
            ASASessionCredentials credentials = new ASASessionCredentials
            {
                AccountId = new Guid(ASAAccountId),
                AccessToken = ASAAccessToken,
                AccountKey = ASAAccountKey,
                AccountDomain = ASAAccountDomain
            };
#if ASA_USE_PREVIEW_PACKAGE
            configuration.OneBoxHost = OneBoxHost;
#endif

            _session.LogLevel = _logLevel;

            // Start the session
            await _session.StartSessionAsync(configuration, credentials);
        }

        /// <summary>
        /// Suspends processing of background tasks.
        /// Soon after this function is called, no more background work will be executed.
        /// </summary>
        /// <remarks>
        /// Call this function when your application is moving into the background to minimize CPU usage.
        ///
        /// This blocks pending operations from completing until the session is resumed.
        /// Calls can still be canceled while the session is paused, but the associated cleanup may be
        /// deferred until the session is resumed or shut down.
        ///
        /// It is safe to call Pause multiple times.
        /// </remarks>
        public void Pause()
        {
            if (_session != null)
            {
                _session.Pause();
            }
        }

        /// <summary>
        /// Resumes processing of background tasks.
        /// </summary>
        /// <remarks>
        /// Call this function when your application needs to resume processing after a call to Pause.
        /// It is safe to call Resume multiple times.
        /// </remarks>
        public void Resume()
        {
            if (_session != null)
            {
                _session.Resume();
            }
        }

        /// <summary>
        /// Sends data to the SDK to aid diagnostics logging by providing anchors to ground truth detections
        /// </summary>
        public async Task RegisterGroundTruthMeasurementForContent(string contentId, ASALocalAnchor asaLocalContent)
        {
            if (!IsSessionStarted())
            {
                // Nothing to do if session is not running
                return;
            }
            await _session.RegisterGroundTruthMeasurementForContent(contentId, asaLocalContent);
        }

        /// <summary>
        /// Enter a map with a known ID for the purpose of extending or localizing.
        /// </summary>
        /// <param name="mapConfiguration">Provides settings for the map to be entered.</param>
        public async Task EnterMapAsync(ASAMapConfiguration mapConfiguration)
        {
            // If session is not running, nothing to do.
            if (!IsSessionStarted())
            {
                throw new InvalidOperationException("Session must be started to enter a map");
            }

            await _session.EnterMap(mapConfiguration);
        }

        /// <summary>
        /// Finish uploading all mapping data collected up to now.
        /// </summary>
        /// <remarks>
        /// To ensure your mapping of a space is complete, you should flush all collected data prior to stopping
        /// the session. If you stop the session or re-enter the map without waiting for all data to be uploaded,
        /// the map may be missing the spaces scanned towards the end of the session and relocalization could
        /// fail.
        /// The payload string is the  ingestion token, which can be called with <c>WaitForMapProcessing</c> to 
        /// determine when the map has been updated on the server to include the environment data collecting during 
        /// this mapping session. This ingestion token can be persisted, or trasmitted to another client that 
        /// wants to wait for the processing of the data collected on the device that did the mapping session.
        /// </remarks>
        /// <returns>
        /// A task which can be used to wait for the flush operation to complete, and the ingestion token corresponding
        /// to this mapping session.
        /// </returns>
        public virtual Task<IngestionToken> FlushMappingData(CancellationToken cancellationToken = default)
        {
            return _session.FlushMappingData(cancellationToken);
        }

        /// <summary>
        /// Waits for the processing of mapping data uploaded by a specific mapping session.
        /// </summary>
        /// <remarks>
        /// The ingestion token is returned when <c>FlushMappingData</c> is called to conclude a mapping session.
        /// The ingestion token does not have to be from the same device, it could have been transmitted from the 
        /// device that did the mapping session using app-layer logic.
        /// await the processing of the mapping session.
        /// </remarks>
        /// <param name="ingestionToken">An ingestion token</param>
        /// <returns></returns>
        public Task WaitForMapProcessing(IngestionToken ingestionToken, CancellationToken cancellationToken = default)
        {
            return _session.WaitForMapProcessing(ingestionToken, cancellationToken);
        }

        public virtual async Task DeleteMap(Guid mapId)
        {
            // If session is not running, nothing to do.
            if (!IsSessionStarted())
            {
                throw new InvalidOperationException("Session must be started to delete a map");
            }

            await _session.DeleteMap(mapId);
        }

        /// <summary>
        /// Method for stopping the underlying <c>ASASession</c>.
        /// </summary>
        public virtual async Task StopSessionAsync()
        {
            if (!IsSessionStarted())
            {
                // StopSession called but no session has been created
                return;
            }

            // Unsubscribe from events
            _session._anchorDiscoveredEvent -= OnAnchorDiscovered;
            _session._sessionLogEvent -= OnSessionLog;
            _session._sessionErrorEvent -= OnSessionError;
            _session._mapExtendedEvent -= OnMapExtended;

            // Stop the session
            await _session.StopSessionAsync();

            _session = null;
        }

        /// <summary>
        /// Retrieve the identifier of the current ASA session as a string.
        /// </summary>
        public string SessionIdentifier()
        {
            if (!IsSessionStarted())
            {
                throw new InvalidOperationException("Session must be started to get identifier");
            }

            return _session.SessionId;
        }

        /// <summary>
        /// Method to create a new map and returns a unique ID.
        /// </summary>
        /// <param name="mapName">Plain text, application friendly name for the map</param>
        /// <returns></returns>
        public virtual async Task<Guid> CreateMapAsync(string mapName)
        {
            if (!AreASACredentialsProvided())
            {
                throw new InvalidOperationException("ASA Account credentials must be provided in the Unity inspector or SetASACredentials()");
            }
            if (!IsSessionStarted())
            {
                throw new InvalidOperationException("Session must be started to create a map");
            }
            return await _session.CreateMap(mapName);
        }

        /// <summary>
        /// Method to list the maps or the ASA account.
        /// </summary>
        /// <returns>Dictionary with map ids and map names and a continuation token that can be used
        /// as parameter for the next call to fetch the remaining data.
        /// </returns>
        public virtual async Task<ASAMapMetaDataPage> EnumerateMapsAsync(string continuationToken)
        {
            if (!AreASACredentialsProvided())
            {
                throw new InvalidOperationException("ASA Account credentials must be provided in the Unity inspector or SetASACredentials()");
            }

            ASAMapMetaDataPage maps = await _session.EnumerateMapNames(continuationToken);
            if (maps == null)
            {
                throw new InvalidOperationException("Failed to retrieve map list");
            }

            return maps;
        }

        /// <summary>
        /// Method to get an ASAAnchor from the service given a pose of the content to be persisted.
        /// Typically, the pose will be defined by the associated GameObject's transform.
        /// </summary>
        /// <param name="contentToWorldPose">Pose of the content in the Unity world coordinate system</param>
        /// <returns>The anchor created/retrieved by the service, and the pose from the content to the anchor</returns>
        public async Task<ContentPlacementRelativeToAnchor> GetAnchorForContentPlacement(Pose contentToWorldPose)
        {
            SpatialAnchor contentSpatialAnchor = null;
#if ENABLE_WINMD_SUPPORT
            contentSpatialAnchor = WinMRInterop.TryCreateAnchor(contentToWorldPose.ToNumericsPose());
#endif
            if (contentSpatialAnchor == null)
            {
               throw new InvalidOperationException("Unable to create SpatialAnchor");
            }

            // Request an anchor from the service
            Tuple<ASAAnchor, Matrix4x4> getAnchorResponse = await _session.GetSpatialAnchor(contentSpatialAnchor);

            ASAAnchor returnedAsaAnchor = getAnchorResponse.Item1;
            Matrix4x4 contentToReturnedAsaAnchorPoseMatrix = getAnchorResponse.Item2;

            if (returnedAsaAnchor == null)
            {
                throw new InvalidOperationException("Failed to get a CloudAnchor");
            }

            Pose contentToReturnedAsaAnchorPose = new Pose(
                position: contentToReturnedAsaAnchorPoseMatrix.GetTranslation(),
                rotation: contentToReturnedAsaAnchorPoseMatrix.GetRotation());

            return new ContentPlacementRelativeToAnchor(
                anchor: returnedAsaAnchor,
                contentToAnchorPose: contentToReturnedAsaAnchorPose);
        }

        /// <summary>
        /// Method to locate an anchor given the the anchor identifier. The anchor must be belong
        /// to the map and the session must be connected. An anchor can only be passed back to the
        /// app once, through either anchor creation, resolve, or discovery.
        /// </summary>
        /// <param name="anchorId">The ASAAnchor identifier</param>
        /// <returns></returns>
        public Task<ASAAnchor> ResolveAnchor(Guid anchorId)
        {
            return _session.ResolveAnchor(anchorId);
        }

        /// <summary>
        /// Method to toggle anchor discovery. As a device moves through the space, we compare the
        /// distance to each anchor. If the device comes within the distanceThreshold, then the
        /// anchor is returned to the application via the <c>AnchorDiscovered</c> event. Must be
        /// connected to the map. An anchor can only be passed back to the app once, through either
        /// anchor creation, resolve, or discovery.
        /// </summary>
        /// <param name="enabled">Whether to enable anchor discovery</param>
        /// <param name="distanceThreshold">The distance in meters to discover anchors</param>
        public virtual void SetDiscovery(bool enabled, float distanceThreshold = float.MaxValue)
        {
            _session.SetDiscovery(enabled, distanceThreshold);
        }

        /// <summary>
        /// Method to handle the <c>ASAAnchorDiscovered</c> event from the <c>ASASession</c>.
        /// </summary>
        private void OnAnchorDiscovered(ASAAnchorDiscoveredEventArgs args)
        {
            AnchorDiscovered?.Invoke(args);
        }

        /// <summary>
        /// Method to handle the <c>ASASessionLog</c> event from the <c>ASASession</c>.
        /// </summary>
        private void OnSessionLog(ASASessionLogEventArgs args)
        {
            SessionLog?.Invoke(args);
        }

        /// <summary>
        /// Method to handle the <c>ASASessionError</c> event from the <c>ASASession</c>.
        /// </summary>
        private void OnSessionError(ASASessionErrorEventArgs args)
        {
            SessionError?.Invoke(args);
        }

        /// <summary>
        /// Method to handle the <c>ASAMapExtended</c> event from the <c>ASASession</c>.
        /// </summary>
        private void OnMapExtended(ASAMapExtendedEventArgs args)
        {
            MapExtended?.Invoke(args);
        }

        /// <summary>
        /// An event that notifies the application that the plugin or the ASA SDK has a log message
        /// to share.
        /// </summary>
        public event ASASessionLogDelegate SessionLog;

        /// <summary>
        /// An event that notifies the application that an error has occurred either in the plugin
        /// or in the ASA SDK. Most errors are handled via exception handling but some errors, like
        /// those in event callbacks, are propagated to the app via this event.
        /// </summary>
        public event ASASessionErrorDelegate SessionError;

        /// <summary>
        /// An event that notifies the application of anchor discovery. <c>DiscoveryEnabled</c>
        /// must be set to true and the application must be connected to the map. Hands back a
        /// reference to the discovered <c>ASAAnchor</c>.
        /// </summary>
        public event ASAAnchorDiscoveredDelegate AnchorDiscovered;

        /// <summary>
        /// Occurs when new mapping data has been uploaded.
        /// </summary>
        public event ASAMapExtendedDelegate MapExtended;
    }

    /// <summary>
    /// Contains the information needed to anchor an item of content, including the anchor
    /// and the position of the content relative to it.
    /// </summary>
    public class ContentPlacementRelativeToAnchor
    {
        internal ContentPlacementRelativeToAnchor(ASAAnchor anchor, Pose contentToAnchorPose)
        {
            Anchor = anchor;
            ContentToAnchorPose = contentToAnchorPose;
        }

        /// <summary>
        /// The ASA anchor to be used
        /// </summary>
        public ASAAnchor Anchor { get; }

        /// <summary>
        /// The pose from the content to the anchor
        /// </summary>
        public Pose ContentToAnchorPose { get; }
    }
}
