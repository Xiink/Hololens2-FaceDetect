using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Devices.Enumeration;
using Windows.Devices.Sensors;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Graphics.Display;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Phone.UI.Input;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Shapes;
using Windows.Media.FaceAnalysis;
using Windows.UI;
using System.Collections.Generic;
using System.Net.Http;
using System.IO;
using System.Xml;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.UI.Xaml.Media.Imaging;
using Microsoft.MixedReality.WebRTC;
using Newtonsoft.Json;
using System.Text;
using FaceRecognition_Simple_Sample.JSON.A2I;
using Windows.Media.Devices;
using Windows.UI.ViewManagement;
using Windows.Networking.Sockets;
using Windows.Networking;
using Windows.ApplicationModel.Core;

// https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x404

namespace FaceDetection
{
    public sealed partial class MainPage : Page
    {
        // Receive notifications about rotation of the device and UI and apply any necessary rotation to the preview stream and UI controls
        private readonly DisplayInformation _displayInformation = DisplayInformation.GetForCurrentView();
        private readonly SimpleOrientationSensor _orientationSensor = SimpleOrientationSensor.GetDefault();
        private SimpleOrientation _deviceOrientation = SimpleOrientation.NotRotated;
        private DisplayOrientations _displayOrientation = DisplayOrientations.Portrait;

        // Rotation metadata to apply to the preview stream and recorded videos (MF_MT_VIDEO_ROTATION)
        // Reference: http://msdn.microsoft.com/en-us/library/windows/apps/xaml/hh868174.aspx
        private static readonly Guid RotationKey = new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");

        // Folder in which the captures will be stored (initialized in SetupUiAsync)
        private StorageFolder _captureFolder = null;

        // Prevent the screen from sleeping while the camera is running
        private readonly DisplayRequest _displayRequest = new DisplayRequest();

        // For listening to media property changes
        private readonly SystemMediaTransportControls _systemMediaControls = SystemMediaTransportControls.GetForCurrentView();

        // MediaCapture and its state variables
        private MediaCapture _mediaCapture;
        private IMediaEncodingProperties _previewProperties;
        private bool _isInitialized;
        private bool _isRecording;

        // Information about the camera device
        private bool _mirroringPreview;
        private bool _externalCamera;

        private FaceDetectionEffect _faceDetectionEffect;

        private string captureStr = "";

        // Advanced Capture and Scene Analysis instances
        private AdvancedPhotoCapture _advancedCapture;
        // Holds the index of the current Advanced Capture mode (-1 for no Advanced Capture active)
        private int _advancedCaptureMode = -1;

        HttpClient Client = new HttpClient();

        /// <summary>
        /// Helper class to contain the information that describes an Advanced Capture
        /// </summary>
        public class AdvancedCaptureContext
        {
            public string CaptureFileName;
            public PhotoOrientation CaptureOrientation;
            public CapturedFrame frame;
        }

        #region Constructor, lifecycle and navigation
        public MainPage() {
            this.InitializeComponent();
            // Do not cache the state of the UI when suspending/navigating
            NavigationCacheMode = NavigationCacheMode.Disabled;

            // Useful to know when to initialize/clean up the camera
            Application.Current.Suspending += Application_Suspending;
            Application.Current.Resuming += Application_Resuming;

            Client.BaseAddress = new Uri("192.168.1.1");
        }

        private async void Application_Suspending(object sender, SuspendingEventArgs e) {
            // Handle global application events only if this page is active
            if (Frame.CurrentSourcePageType == typeof(MainPage))
            {
                var deferral = e.SuspendingOperation.GetDeferral();

                await CleanupCameraAsync();

                await CleanupUiAsync();

                deferral.Complete();
            }
        }

        private async void Application_Resuming(object sender, object o) {
            // Handle global application events only if this page is active
            if (Frame.CurrentSourcePageType == typeof(MainPage))
            {
                await SetupUiAsync();

                await InitializeCameraAsync();
            }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e) {
            await SetupUiAsync();

            await InitializeCameraAsync();
        }

        protected override async void OnNavigatingFrom(NavigatingCancelEventArgs e) {
            // Handling of this event is included for completenes, as it will only fire when navigating between pages and this sample only includes one page

            await CleanupCameraAsync();

            await CleanupUiAsync();
        }

        #endregion Constructor, lifecycle and navigation

        #region Event handlers

        /// <summary>
        /// In the event of the app being minimized this method handles media property change events. If the app receives a mute
        /// notification, it is no longer in the foregroud.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private async void SystemMediaControls_PropertyChanged(SystemMediaTransportControls sender, SystemMediaTransportControlsPropertyChangedEventArgs args) {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                // Only handle this event if this page is currently being displayed
                if (args.Property == SystemMediaTransportControlsProperty.SoundLevel && Frame.CurrentSourcePageType == typeof(MainPage))
                {
                    // Check to see if the app is being muted. If so, it is being minimized.
                    // Otherwise if it is not initialized, it is being brought into focus.
                    if (sender.SoundLevel == SoundLevel.Muted)
                    {
                        await CleanupCameraAsync();
                    }
                    else if (!_isInitialized)
                    {
                        await InitializeCameraAsync();
                    }
                }
            });
        }

        /// <summary>
        /// Occurs each time the simple orientation sensor reports a new sensor reading.
        /// </summary>
        /// <param name="sender">The event source.</param>
        /// <param name="args">The event data.</param>
        private async void OrientationSensor_OrientationChanged(SimpleOrientationSensor sender, SimpleOrientationSensorOrientationChangedEventArgs args) {
            if (args.Orientation != SimpleOrientation.Faceup && args.Orientation != SimpleOrientation.Facedown)
            {
                // Only update the current orientation if the device is not parallel to the ground. This allows users to take pictures of documents (FaceUp)
                // or the ceiling (FaceDown) in portrait or landscape, by first holding the device in the desired orientation, and then pointing the camera
                // either up or down, at the desired subject.
                //Note: This assumes that the camera is either facing the same way as the screen, or the opposite way. For devices with cameras mounted
                //      on other panels, this logic should be adjusted.
                _deviceOrientation = args.Orientation;

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => UpdateButtonOrientation());
            }
        }

        /// <summary>
        /// This event will fire when the page is rotated, when the DisplayInformation.AutoRotationPreferences value set in the SetupUiAsync() method cannot be not honored.
        /// </summary>
        /// <param name="sender">The event source.</param>
        /// <param name="args">The event data.</param>
        private async void DisplayInformation_OrientationChanged(DisplayInformation sender, object args) {
            _displayOrientation = sender.CurrentOrientation;

            if (_previewProperties != null)
            {
                await SetPreviewRotationAsync();
            }

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => UpdateButtonOrientation());
        }

        private async void PhotoButton_Click(object sender, RoutedEventArgs e) {
            var time = Stopwatch.StartNew();
            await TakeAdvancePhotoAsync(new BitmapBounds() { X = 0, Y = 0, Width = 0, Height = 0 });
            time.Stop();
            Debug.WriteLine("Capture time : " + time.ElapsedMilliseconds);
        }

        private async void VideoButton_Click(object sender, RoutedEventArgs e) {
            if (!_isRecording)
            {
                await StartRecordingAsync();
            }
            else
            {
                await StopRecordingAsync();
            }

            // After starting or stopping video recording, update the UI to reflect the MediaCapture state
            UpdateCaptureControls();
        }

        private async void TestConnectButton_Click(object sender, RoutedEventArgs e) {
        }

        private async void FaceDetectionButton_Click(object sender, RoutedEventArgs e) {
            if (_faceDetectionEffect == null || !_faceDetectionEffect.Enabled)
            {
                // Clear any rectangles that may have been left over from a previous instance of the effect
                FacesCanvas.Children.Clear();

                await CreateFaceDetectionEffectAsync();
            }
            else
            {
                await CleanUpFaceDetectionEffectAsync();
            }

            UpdateCaptureControls();
        }

        private async void HardwareButtons_CameraPressed(object sender, CameraEventArgs e) {
            //await TakePhotoAsync();
            await TakeAdvancePhotoAsync(new BitmapBounds() { X = 0, Y = 0, Width = 200, Height = 200 });
        }

        private async void MediaCapture_RecordLimitationExceeded(MediaCapture sender) {
            // This is a notification that recording has to stop, and the app is expected to finalize the recording

            await StopRecordingAsync();

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => UpdateCaptureControls());
        }

        private async void MediaCapture_Failed(MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs) {
            Debug.WriteLine("MediaCapture_Failed: (0x{0:X}) {1}", errorEventArgs.Code, errorEventArgs.Message);

            await CleanupCameraAsync();

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => UpdateCaptureControls());
        }

        private async void FaceDetectionEffect_FaceDetected(FaceDetectionEffect sender, FaceDetectedEventArgs args) {
            // Ask the UI thread to render the face bounding boxes
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => HighlightDetectedFaces(args.ResultFrame.DetectedFaces));
        }

        #endregion Event handlers

        #region MediaCapture methods

        /// <summary>
        /// Init MediaCaptur
        /// </summary>
        /// <returns></returns>
        private async Task InitializeCameraAsync() {
            Debug.WriteLine("InitializeCameraAsync");

            if (_mediaCapture == null)
            {
                // Attempt to get the front camera if one is available, but use any camera device if not
                var cameraDevice = await FindCameraDeviceByPanelAsync(Windows.Devices.Enumeration.Panel.Front);

                if (cameraDevice == null)
                {
                    Debug.WriteLine("No camera device found!");
                    return;
                }

                // Create MediaCapture and its settings
                _mediaCapture = new MediaCapture();

                // Register for a notification when video recording has reached the maximum time and when something goes wrong
                _mediaCapture.RecordLimitationExceeded += MediaCapture_RecordLimitationExceeded;
                _mediaCapture.Failed += MediaCapture_Failed;

                // Create Camera Setting
                var settings = new MediaCaptureInitializationSettings { VideoDeviceId = cameraDevice.Id };

                // Init MediaCapture
                try
                {
                    await _mediaCapture.InitializeAsync(settings);
                    _isInitialized = true;
                }
                catch (UnauthorizedAccessException)
                {
                    Debug.WriteLine("The app was denied access to the camera");
                }

                // If initialization succeeded, start the preview
                if (_isInitialized)
                {
                    // Figure out where the camera is located
                    if (cameraDevice.EnclosureLocation == null || cameraDevice.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Unknown)
                    {
                        // No information on the location of the camera, assume it's an external camera, not integrated on the device
                        _externalCamera = true;
                    }
                    else
                    {
                        // Camera is fixed on the device
                        _externalCamera = false;

                        // Only mirror the preview if thfrue camera is on the front panel
                        _mirroringPreview = (cameraDevice.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Front);
                    }

                    await StartPreviewAsync();

                    await EnableAdvancedCaptureAsync();

                    UpdateCaptureControls();
                }
            }
        }

        /// <summary>
        /// Start Preview
        /// </summary>
        /// <returns></returns>
        private async Task StartPreviewAsync() {
            // Prevent the device from sleeping while the preview is running
            _displayRequest.RequestActive();

            // Set image source
            PreviewControl.Source = _mediaCapture;
            PreviewControl.FlowDirection = _mirroringPreview ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            PreviewControl.Opacity = 0;

            // Start preview
            await _mediaCapture.StartPreviewAsync();
            _previewProperties = _mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);

            // Initialize the preview to the current orientation
            if (_previewProperties != null)
            {
                _displayOrientation = _displayInformation.CurrentOrientation;

                await SetPreviewRotationAsync();
            }
        }

        /// <summary>
        /// Gets the current orientation of the UI in relation to the device (when AutoRotationPreferences cannot be honored) and applies a corrective rotation to the preview
        /// </summary>
        private async Task SetPreviewRotationAsync() {
            // Only need to update the orientation if the camera is mounted on the device
            if (_externalCamera) return;

            // Calculate which way and how far to rotate the preview
            int rotationDegrees = ConvertDisplayOrientationToDegrees(_displayOrientation);

            // The rotation direction needs to be inverted if the preview is being mirrored
            if (_mirroringPreview)
            {
                rotationDegrees = (360 - rotationDegrees) % 360;
            }

            // Add rotation metadata to the preview stream to make sure the aspect ratio / dimensions match when rendering and getting preview frames
            var props = _mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);
            props.Properties.Add(RotationKey, rotationDegrees);
            await _mediaCapture.SetEncodingPropertiesAsync(MediaStreamType.VideoPreview, props, null);
        }

        /// <summary>
        /// Stops the preview and deactivates a display request, to allow the screen to go into power saving modes
        /// </summary>
        /// <returns></returns>
        private async Task StopPreviewAsync() {
            // Stop the preview
            _previewProperties = null;
            await _mediaCapture.StopPreviewAsync();

            // Use the dispatcher because this method is sometimes called from non-UI threads
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                // Cleanup the UI
                PreviewControl.Source = null;

                // Allow the device screen to sleep now that the preview is stopped
                _displayRequest.RequestRelease();
            });
        }

        /// <summary>
        /// Add Face Detect Fuction
        /// </summary>
        /// <returns></returns>
        private async Task CreateFaceDetectionEffectAsync() {
            var definition = new FaceDetectionEffectDefinition();

            // To ensure preview smoothness, do not delay incoming samples
            definition.SynchronousDetectionEnabled = false;
            definition.DetectionMode = FaceDetectionMode.HighPerformance;
            _faceDetectionEffect = (FaceDetectionEffect)await _mediaCapture.AddVideoEffectAsync(definition, MediaStreamType.VideoPreview);
            // When Detect Face
            _faceDetectionEffect.FaceDetected += FaceDetectionEffect_FaceDetected;
            _faceDetectionEffect.DesiredDetectionInterval = TimeSpan.FromMilliseconds(1500);
            _faceDetectionEffect.Enabled = true;
        }

        /// <summary>
        /// Disable Face Detect Fuction
        /// </summary>
        /// <returns></returns>
        private async Task CleanUpFaceDetectionEffectAsync() {
            _faceDetectionEffect.Enabled = false;

            _faceDetectionEffect.FaceDetected -= FaceDetectionEffect_FaceDetected;

            await _mediaCapture.RemoveEffectAsync(_faceDetectionEffect);

            _faceDetectionEffect = null;
        }

        /// <summary>
        /// Init Advcnced Capture
        /// </summary>
        /// <returns></returns>
        private async Task EnableAdvancedCaptureAsync() {
            // Init in first time
            if (_advancedCapture != null) return;

            // Configure one of the modes in the control
            CycleAdvancedCaptureMode();

            //測試用ImageEncodingProperties不支援圖片縮放
            var encoding = ImageEncodingProperties.CreateJpeg();
            encoding.Width = 1280;
            encoding.Height = 720;

            // Prepare for an Advanced Capture
            _advancedCapture = await _mediaCapture.PrepareAdvancedPhotoCaptureAsync(ImageEncodingProperties.CreateJpeg());
            Debug.WriteLine("Enabled Advanced Capture");

            // Register for events published by the AdvancedCapture
            _advancedCapture.AllPhotosCaptured += AdvancedCapture_AllPhotosCaptured;
            //_advancedCapture.OptionalReferencePhotoCaptured += AdvancedCapture_OptionalReferencePhotoCaptured;
        }

        /// <summary>
        /// This event will be raised when all the necessary captures are completed, and at this point the camera is technically ready
        /// to capture again while the processing takes place.
        /// </summary>
        /// <param name="sender">The object raising this event.</param>
        /// <param name="args">The event data.</param>
        private void AdvancedCapture_AllPhotosCaptured(AdvancedPhotoCapture sender, object args) {
            Debug.WriteLine("AdvancedCapture_AllPhotosCaptured");

        }

        /// <summary>
        /// Configures the AdvancedPhotoControl to the next supported mode
        /// </summary>
        /// <remarks>
        /// Note that this method can be safely called regardless of whether the AdvancedPhotoCapture is in the "prepared
        /// state" or not. Internal changes to the mode will be applied  when calling Prepare, or when calling Capture if
        /// the mode has been changed since the call to Prepare. This allows for fast changing of desired capture modes,
        /// that can more quickly adapt to rapidly changing shooting conditions.
        /// </remarks>
        private void CycleAdvancedCaptureMode() {
            // Calculate the index for the next supported mode
            _advancedCaptureMode = (_advancedCaptureMode + 1) % _mediaCapture.VideoDeviceController.AdvancedPhotoControl.SupportedModes.Count;

            // Configure the settings object to the mode at the calculated index
            var settings = new AdvancedPhotoCaptureSettings
            {
                Mode = _mediaCapture.VideoDeviceController.AdvancedPhotoControl.SupportedModes[_advancedCaptureMode]
            };

            // Configure the mode on the control
            _mediaCapture.VideoDeviceController.AdvancedPhotoControl.Configure(settings);

            // Update the button text to reflect the current mode
            Debug.WriteLine(_mediaCapture.VideoDeviceController.AdvancedPhotoControl.Mode.ToString());
            //ModeTextBlock.Text = _mediaCapture.VideoDeviceController.AdvancedPhotoControl.Mode.ToString();
        }

        /// <summary>
        /// DisableAdvancedCapture
        /// </summary>
        /// <returns></returns>
        private async Task DisableAdvancedCaptureAsync() {
            // No work to be done if there is no AdvancedCapture
            if (_advancedCapture == null) return;

            await _advancedCapture.FinishAsync();
            _advancedCapture = null;

            // Reset the Advanced Capture Mode index
            _advancedCaptureMode = -1;

            Debug.WriteLine("Disabled Advanced Capture");
        }

        /// <summary>
        /// Take Photo and convert to Base64
        /// </summary>
        /// <returns></returns>
        private async Task TakePhotoAsync() {
            // While taking a photo, keep the video button enabled only if the camera supports simultaneously taking pictures and recording video
            VideoButton.IsEnabled = _mediaCapture.MediaCaptureSettings.ConcurrentRecordAndPhotoSupported;

            // Make the button invisible if it's disabled, so it's obvious it cannot be interacted with
            VideoButton.Opacity = VideoButton.IsEnabled ? 1 : 0;

            try
            {
                var lowLagCapture = await _mediaCapture.PrepareLowLagPhotoCaptureAsync(ImageEncodingProperties.CreateUncompressed(MediaPixelFormat.Bgra8));
                var capturedPhoto = await lowLagCapture.CaptureAsync();
                var softwareBitmap = capturedPhoto.Frame.SoftwareBitmap;
                await lowLagCapture.FinishAsync();

                // Transform SoftBitmap to Base64
                var data = await EncodedBytes(softwareBitmap, BitmapEncoder.JpegEncoderId);
                captureStr = Convert.ToBase64String(data);
                Debug.WriteLine("Photo saved!");
            }
            catch (Exception ex)
            {
                // File I/O errors are reported as exceptions
                Debug.WriteLine("Exception when taking a photo: " + ex.ToString());
            }

            // Done taking a photo, so re-enable the button
            VideoButton.IsEnabled = true;
            VideoButton.Opacity = 1;
        }



        /// <summary>
        /// Advance Photo Capture
        /// </summary>
        /// <returns></returns>
        private async Task<string> TakeAdvancePhotoAsync(BitmapBounds bounds) {
            try
            {
                Debug.WriteLine("Taking Advanced Capture photo...");
                var photoOrientation = ConvertOrientationToPhotoOrientation(GetCameraOrientation());
                var fileName = string.Format("AdvancedCapturePhoto_{0}.bmp", DateTime.Now.ToString("HHmmss"));

                // Create a context object, to identify the capture later on
                var context = new AdvancedCaptureContext { CaptureFileName = fileName, CaptureOrientation = photoOrientation  };
                // Start capture, and pass the context object to get it back in the OptionalReferencePhotoCaptured event
                var capture = await _advancedCapture.CaptureAsync(context);

                using (var frame = capture.Frame)
                {
                    var bytes = await EncodedBytes(frame, photoOrientation, bounds);
                    var str = Convert.ToBase64String(bytes);
                    captureStr = str;
                    Debug.WriteLine(str);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Exception when taking an Advanced Capture photo: " + ex.ToString());
            }
            return null;
        }

        /// <summary>
        /// Save SoftBitmap to Bitmap
        /// </summary>
        /// <param name="softwareBitmap"></param>
        /// <param name="outputFile"></param>
        private async void SaveSoftwareBitmapToFile(SoftwareBitmap softwareBitmap, StorageFile outputFile) {
            using (IRandomAccessStream stream = await outputFile.OpenAsync(FileAccessMode.ReadWrite))
            {
                byte[] array = null;
                // Create an encoder with the desired format
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);

                // Set the software bitmap
                encoder.SetSoftwareBitmap(softwareBitmap);

                // Set additional encoding parameters, if needed
                encoder.BitmapTransform.ScaledWidth = 2272;
                encoder.BitmapTransform.ScaledHeight = 1278;
                //encoder.BitmapTransform.Rotation = Windows.Graphics.Imaging.BitmapRotation.Clockwise90Degrees;
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                encoder.IsThumbnailGenerated = true;

                try
                {
                    await encoder.FlushAsync();
                }
                catch (Exception err)
                {
                    const int WINCODEC_ERR_UNSUPPORTEDOPERATION = unchecked((int)0x88982F81);
                    switch (err.HResult)
                    {
                        case WINCODEC_ERR_UNSUPPORTEDOPERATION:
                        // If the encoder does not support writing a thumbnail, then try again
                        // but disable thumbnail generation.
                        encoder.IsThumbnailGenerated = false;
                        break;
                        default:
                        throw;
                    }
                }

                if (encoder.IsThumbnailGenerated == false)
                {
                    await encoder.FlushAsync();
                }
            }
        }

        /// <summary>
        /// Transform SoftBitmap to Base64
        /// </summary>
        /// <param name="soft"></param>
        /// <param name="encoderId"></param>
        /// <returns></returns>
        private async Task<byte[]> EncodedBytes(SoftwareBitmap soft, Guid encoderId) {
            byte[] array = null;
            using (var ms = new InMemoryRandomAccessStream())
            {
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(encoderId, ms);
                encoder.SetSoftwareBitmap(soft);
                encoder.BitmapTransform.ScaledWidth = 2272;
                encoder.BitmapTransform.ScaledHeight = 1278;
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                try
                {
                    await encoder.FlushAsync();
                }
                catch (Exception ex) { return new byte[0]; }
                array = new byte[ms.Size];
                await ms.ReadAsync(array.AsBuffer(), (uint)ms.Size, InputStreamOptions.None);
            }
            return array;
        }

        /// <summary>
        /// Transform SoftBitmap to Base64(CaptureFrame)
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="photoOrientation"></param>
        /// <returns></returns>
        private async Task<byte[]> EncodedBytes(IRandomAccessStream stream, PhotoOrientation photoOrientation, BitmapBounds bounds) {
            byte[] array = null;
            using (var inputStream = stream)
            {
                var decoder = await BitmapDecoder.CreateAsync(inputStream);
                using (var ms = new InMemoryRandomAccessStream())
                {
                    var encoder = await BitmapEncoder.CreateForTranscodingAsync(ms, decoder);
                    var properties = new BitmapPropertySet { { "System.Photo.Orientation", new BitmapTypedValue(photoOrientation, PropertyType.UInt16) } };
                    encoder.BitmapTransform.ScaledWidth = 2278;
                    encoder.BitmapTransform.ScaledHeight = 1278;
                    encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;

                    encoder.BitmapTransform.Bounds = bounds;

                    await encoder.BitmapProperties.SetPropertiesAsync(properties);
                    await encoder.FlushAsync();
                    array = new byte[ms.Size];
                    await ms.ReadAsync(array.AsBuffer(), (uint)ms.Size, InputStreamOptions.None);
                }
            }
            return array;
        }

        /// <summary>
        /// Recoding and Save MP4 format Video
        /// </summary>
        /// <returns></returns>
        private async Task StartRecordingAsync() {
            try
            {
                // Create storage file for the capture
                var videoFile = await _captureFolder.CreateFileAsync("SimpleVideo.mp4", CreationCollisionOption.GenerateUniqueName);

                var encodingProfile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);

                // Calculate rotation angle, taking mirroring into account if necessary
                var rotationAngle = 360 - ConvertDeviceOrientationToDegrees(GetCameraOrientation());
                encodingProfile.Video.Properties.Add(RotationKey, PropertyValue.CreateInt32(rotationAngle));

                Debug.WriteLine("Starting recording to " + videoFile.Path);

                await _mediaCapture.StartRecordToStorageFileAsync(encodingProfile, videoFile);
                _isRecording = true;

                Debug.WriteLine("Started recording!");
            }
            catch (Exception ex)
            {
                // File I/O errors are reported as exceptions
                Debug.WriteLine("Exception when starting video recording: " + ex.ToString());
            }
        }

        /// <summary>
        /// Stop Recording Video
        /// </summary>
        /// <returns></returns>
        private async Task StopRecordingAsync() {
            Debug.WriteLine("Stopping recording...");

            _isRecording = false;
            await _mediaCapture.StopRecordAsync();

            Debug.WriteLine("Stopped recording!");
        }

        /// <summary>
        /// Cleans up the camera resources (after stopping any video recording and/or preview if necessary) and unregisters from MediaCapture events
        /// </summary>
        /// <returns></returns>
        private async Task CleanupCameraAsync() {
            Debug.WriteLine("CleanupCameraAsync");

            if (_isInitialized)
            {
                // If a recording is in progress during cleanup, stop it to save the recording
                if (_isRecording)
                {
                    await StopRecordingAsync();
                }

                if (_faceDetectionEffect != null)
                {
                    await CleanUpFaceDetectionEffectAsync();
                }

                if (_advancedCapture != null)
                {
                    //await DisableAdvancedCaptureAsync();
                }

                if (_previewProperties != null)
                {
                    // The call to stop the preview is included here for completeness, but can be
                    // safely removed if a call to MediaCapture.Dispose() is being made later,
                    // as the preview will be automatically stopped at that point
                    await StopPreviewAsync();
                }

                _isInitialized = false;
            }

            if (_mediaCapture != null)
            {
                _mediaCapture.RecordLimitationExceeded -= MediaCapture_RecordLimitationExceeded;
                _mediaCapture.Failed -= MediaCapture_Failed;
                _mediaCapture.Dispose();
                _mediaCapture = null;
            }
        }

        #endregion MediaCapture methods


        #region Helper functions

        /// <summary>
        /// Attempts to lock the page orientation, hide the StatusBar (on Phone) and registers event handlers for hardware buttons and orientation sensors
        /// </summary>
        /// <returns></returns>
        private async Task SetupUiAsync() {
            // Attempt to lock page to landscape orientation to prevent the CaptureElement from rotating, as this gives a better experience
            DisplayInformation.AutoRotationPreferences = DisplayOrientations.Landscape;

            // Hide the status bar
            if (ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar"))
            {
                await Windows.UI.ViewManagement.StatusBar.GetForCurrentView().HideAsync();
            }

            // Populate orientation variables with the current state
            _displayOrientation = _displayInformation.CurrentOrientation;
            if (_orientationSensor != null)
            {
                _deviceOrientation = _orientationSensor.GetCurrentOrientation();
            }

            RegisterEventHandlers();

            var picturesLibrary = await StorageLibrary.GetLibraryAsync(KnownLibraryId.Pictures);
            // Fall back to the local app storage if the Pictures Library is not available
            _captureFolder = picturesLibrary.SaveFolder ?? ApplicationData.Current.LocalFolder;
        }

        /// <summary>
        /// Unregisters event handlers for hardware buttons and orientation sensors, allows the StatusBar (on Phone) to show, and removes the page orientation lock
        /// </summary>
        /// <returns></returns>
        private async Task CleanupUiAsync() {
            UnregisterEventHandlers();

            // Show the status bar
            if (ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar"))
            {
                await Windows.UI.ViewManagement.StatusBar.GetForCurrentView().ShowAsync();
            }

            // Revert orientation preferences
            DisplayInformation.AutoRotationPreferences = DisplayOrientations.None;
        }

        /// <summary>
        /// This method will update the icons, enable/disable and show/hide the photo/video buttons depending on the current state of the app and the capabilities of the device
        /// </summary>
        private void UpdateCaptureControls() {
            // The buttons should only be enabled if the preview started sucessfully
            PhotoButton.IsEnabled = _previewProperties != null;
            VideoButton.IsEnabled = _previewProperties != null;
            FaceDetectionButton.IsEnabled = _previewProperties != null;
            TestConnectButton.IsEnabled = _previewProperties != null;

            // Update the face detection icon depending on whether the effect exists or not
            FaceDetectionDisabledIcon.Visibility = (_faceDetectionEffect == null || !_faceDetectionEffect.Enabled) ? Visibility.Visible : Visibility.Collapsed;
            FaceDetectionEnabledIcon.Visibility = (_faceDetectionEffect != null && _faceDetectionEffect.Enabled) ? Visibility.Visible : Visibility.Collapsed;

            // Hide the face detection canvas and clear it
            FacesCanvas.Visibility = (_faceDetectionEffect != null && _faceDetectionEffect.Enabled) ? Visibility.Visible : Visibility.Collapsed;

            // Update recording button to show "Stop" icon instead of red "Record" icon when recording
            StartRecordingIcon.Visibility = _isRecording ? Visibility.Collapsed : Visibility.Visible;
            StopRecordingIcon.Visibility = _isRecording ? Visibility.Visible : Visibility.Collapsed;

            // If the camera doesn't support simultaneosly taking pictures and recording video, disable the photo button on record
            if (_isInitialized && !_mediaCapture.MediaCaptureSettings.ConcurrentRecordAndPhotoSupported)
            {
                PhotoButton.IsEnabled = !_isRecording;

                // Make the button invisible if it's disabled, so it's obvious it cannot be interacted with
                PhotoButton.Opacity = PhotoButton.IsEnabled ? 1 : 0;
            }
        }

        /// <summary>
        /// Registers event handlers for hardware buttons and orientation sensors, and performs an initial update of the UI rotation
        /// </summary>
        private void RegisterEventHandlers() {
            if (ApiInformation.IsTypePresent("Windows.Phone.UI.Input.HardwareButtons"))
            {
                HardwareButtons.CameraPressed += HardwareButtons_CameraPressed;
            }

            // If there is an orientation sensor present on the device, register for notifications
            if (_orientationSensor != null)
            {
                _orientationSensor.OrientationChanged += OrientationSensor_OrientationChanged;

                // Update orientation of buttons with the current orientation
                UpdateButtonOrientation();
            }

            _displayInformation.OrientationChanged += DisplayInformation_OrientationChanged;
            _systemMediaControls.PropertyChanged += SystemMediaControls_PropertyChanged;
        }

        /// <summary>
        /// Unregisters event handlers for hardware buttons and orientation sensors
        /// </summary>
        private void UnregisterEventHandlers() {
            if (ApiInformation.IsTypePresent("Windows.Phone.UI.Input.HardwareButtons"))
            {
                HardwareButtons.CameraPressed -= HardwareButtons_CameraPressed;
            }

            if (_orientationSensor != null)
            {
                _orientationSensor.OrientationChanged -= OrientationSensor_OrientationChanged;
            }

            _displayInformation.OrientationChanged -= DisplayInformation_OrientationChanged;
            _systemMediaControls.PropertyChanged -= SystemMediaControls_PropertyChanged;
        }

        /// <summary>
        /// Attempts to find and return a device mounted on the panel specified, and on failure to find one it will return the first device listed
        /// </summary>
        /// <param name="desiredPanel">The desired panel on which the returned device should be mounted, if available</param>
        /// <returns></returns>
        private static async Task<DeviceInformation> FindCameraDeviceByPanelAsync(Windows.Devices.Enumeration.Panel desiredPanel) {
            // Get available devices for capturing pictures
            var allVideoDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

            // Get the desired camera by panel
            DeviceInformation desiredDevice = allVideoDevices.FirstOrDefault(x => x.EnclosureLocation != null && x.EnclosureLocation.Panel == desiredPanel);

            // If there is no device mounted on the desired panel, return the first device found
            return desiredDevice ?? allVideoDevices.FirstOrDefault();
        }

        /// <summary>
        /// Applies the given orientation to a photo stream and saves it as a StorageFile
        /// </summary>
        /// <param name="stream">The photo stream</param>
        /// <param name="file">The StorageFile in which the photo stream will be saved</param>
        /// <param name="photoOrientation">The orientation metadata to apply to the photo</param>
        /// <returns></returns>
        private static async Task ReencodeAndSavePhotoAsync(IRandomAccessStream stream, StorageFile file, PhotoOrientation photoOrientation) {
            using (var inputStream = stream)
            {
                var decoder = await BitmapDecoder.CreateAsync(inputStream);

                using (var outputStream = await file.OpenAsync(FileAccessMode.ReadWrite))
                {
                    var encoder = await BitmapEncoder.CreateForTranscodingAsync(outputStream, decoder);

                    var properties = new BitmapPropertySet { { "System.Photo.Orientation", new BitmapTypedValue(photoOrientation, PropertyType.UInt16) } };

                    await encoder.BitmapProperties.SetPropertiesAsync(properties);
                    await encoder.FlushAsync();
                }
            }
        }

        #endregion Helper functions


        #region Rotation helpers

        /// <summary>
        /// Calculates the current camera orientation from the device orientation by taking into account whether the camera is external or facing the user
        /// </summary>
        /// <returns>The camera orientation in space, with an inverted rotation in the case the camera is mounted on the device and is facing the user</returns>
        private SimpleOrientation GetCameraOrientation() {
            if (_externalCamera)
            {
                // Cameras that are not attached to the device do not rotate along with it, so apply no rotation
                return SimpleOrientation.NotRotated;
            }

            var result = _deviceOrientation;

            // Account for the fact that, on portrait-first devices, the camera sensor is mounted at a 90 degree offset to the native orientation
            if (_displayInformation.NativeOrientation == DisplayOrientations.Portrait)
            {
                switch (result)
                {
                    case SimpleOrientation.Rotated90DegreesCounterclockwise:
                    result = SimpleOrientation.NotRotated;
                    break;
                    case SimpleOrientation.Rotated180DegreesCounterclockwise:
                    result = SimpleOrientation.Rotated90DegreesCounterclockwise;
                    break;
                    case SimpleOrientation.Rotated270DegreesCounterclockwise:
                    result = SimpleOrientation.Rotated180DegreesCounterclockwise;
                    break;
                    case SimpleOrientation.NotRotated:
                    result = SimpleOrientation.Rotated270DegreesCounterclockwise;
                    break;
                }
            }

            // If the preview is being mirrored for a front-facing camera, then the rotation should be inverted
            if (_mirroringPreview)
            {
                // This only affects the 90 and 270 degree cases, because rotating 0 and 180 degrees is the same clockwise and counter-clockwise
                switch (result)
                {
                    case SimpleOrientation.Rotated90DegreesCounterclockwise:
                    return SimpleOrientation.Rotated270DegreesCounterclockwise;
                    case SimpleOrientation.Rotated270DegreesCounterclockwise:
                    return SimpleOrientation.Rotated90DegreesCounterclockwise;
                }
            }

            return result;
        }

        /// <summary>
        /// Converts the given orientation of the device in space to the corresponding rotation in degrees
        /// </summary>
        /// <param name="orientation">The orientation of the device in space</param>
        /// <returns>An orientation in degrees</returns>
        private static int ConvertDeviceOrientationToDegrees(SimpleOrientation orientation) {
            switch (orientation)
            {
                case SimpleOrientation.Rotated90DegreesCounterclockwise:
                return 90;
                case SimpleOrientation.Rotated180DegreesCounterclockwise:
                return 180;
                case SimpleOrientation.Rotated270DegreesCounterclockwise:
                return 270;
                case SimpleOrientation.NotRotated:
                default:
                return 0;
            }
        }

        /// <summary>
        /// Converts the given orientation of the app on the screen to the corresponding rotation in degrees
        /// </summary>
        /// <param name="orientation">The orientation of the app on the screen</param>
        /// <returns>An orientation in degrees</returns>
        private static int ConvertDisplayOrientationToDegrees(DisplayOrientations orientation) {
            switch (orientation)
            {
                case DisplayOrientations.Portrait:
                return 90;
                case DisplayOrientations.LandscapeFlipped:
                return 180;
                case DisplayOrientations.PortraitFlipped:
                return 270;
                case DisplayOrientations.Landscape:
                default:
                return 0;
            }
        }

        /// <summary>
        /// Converts the given orientation of the device in space to the metadata that can be added to captured photos
        /// </summary>
        /// <param name="orientation">The orientation of the device in space</param>
        /// <returns></returns>
        private static PhotoOrientation ConvertOrientationToPhotoOrientation(SimpleOrientation orientation) {
            switch (orientation)
            {
                case SimpleOrientation.Rotated90DegreesCounterclockwise:
                return PhotoOrientation.Rotate90;
                case SimpleOrientation.Rotated180DegreesCounterclockwise:
                return PhotoOrientation.Rotate180;
                case SimpleOrientation.Rotated270DegreesCounterclockwise:
                return PhotoOrientation.Rotate270;
                case SimpleOrientation.NotRotated:
                default:
                return PhotoOrientation.Normal;
            }
        }

        /// <summary>
        /// Uses the current device orientation in space and page orientation on the screen to calculate the rotation
        /// transformation to apply to the controls
        /// </summary>
        /// <returns>An angle in degrees to rotate the controls so they remain upright to the user regardless of device and page
        /// orientation</returns>
        private void UpdateButtonOrientation() {
            int device = ConvertDeviceOrientationToDegrees(_deviceOrientation);
            int display = ConvertDisplayOrientationToDegrees(_displayOrientation);

            if (_displayInformation.NativeOrientation == DisplayOrientations.Portrait)
            {
                device -= 90;
            }

            // Combine both rotations and make sure that 0 <= result < 360
            var angle = (360 + display + device) % 360;

            // Rotate the buttons in the UI to match the rotation of the device
            var transform = new RotateTransform { Angle = angle };

            // The RenderTransform is safe to use (i.e. it won't cause layout issues) in this case, because these buttons have a 1:1 aspect ratio
            PhotoButton.RenderTransform = transform;
            VideoButton.RenderTransform = transform;
            FaceDetectionButton.RenderTransform = transform;

            TestConnectButton.RenderTransform = transform;
        }

        /// <summary>
        /// Uses the current display orientation to calculate the rotation transformation to apply to the face detection bounding box canvas
        /// and mirrors it if the preview is being mirrored
        /// </summary>
        private void SetFacesCanvasRotation() {
            // Calculate how much to rotate the canvas
            int rotationDegrees = ConvertDisplayOrientationToDegrees(_displayOrientation);

            // The rotation direction needs to be inverted if the preview is being mirrored, just like in SetPreviewRotationAsync
            if (_mirroringPreview)
            {
                rotationDegrees = (360 - rotationDegrees) % 360;
            }

            // Apply the rotation
            var transform = new RotateTransform { Angle = rotationDegrees };
            FacesCanvas.RenderTransform = transform;

            var previewArea = GetPreviewStreamRectInControl(_previewProperties as VideoEncodingProperties, PreviewControl);

            // For portrait mode orientations, swap the width and height of the canvas after the rotation, so the control continues to overlap the preview
            if (_displayOrientation == DisplayOrientations.Portrait || _displayOrientation == DisplayOrientations.PortraitFlipped)
            {
                FacesCanvas.Width = previewArea.Height;
                FacesCanvas.Height = previewArea.Width;

                // The position of the canvas also needs to be adjusted, as the size adjustment affects the centering of the control
                Canvas.SetLeft(FacesCanvas, previewArea.X - (previewArea.Height - previewArea.Width) / 2);
                Canvas.SetTop(FacesCanvas, previewArea.Y - (previewArea.Width - previewArea.Height) / 2);
            }
            else
            {
                FacesCanvas.Width = previewArea.Width;
                FacesCanvas.Height = previewArea.Height;

                Canvas.SetLeft(FacesCanvas, previewArea.X);
                Canvas.SetTop(FacesCanvas, previewArea.Y);
            }

            // Also mirror the canvas if the preview is being mirrored
            FacesCanvas.FlowDirection = _mirroringPreview ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        }

        #endregion Rotation helpers


        #region Face detection helpers

        /// <summary>
        /// 
        /// </summary>
        /// <param name="faces">The list of detected faces from the FaceDetected event of the effect</param>
        private async void HighlightDetectedFaces(IReadOnlyList<DetectedFace> faces) {
            //await TakePhotoAsync();

            List<Rectangle> faceBoundingBoxs = new List<Rectangle>();
            List<string> usernames = new List<string>();
            List<Point> points = new List<Point>();

            for (int i = 0; i < faces.Count; i++)
            {

                var username = "";

                // Coordinate Trasform
                Rectangle faceBoundingBox = ConvertPreviewToUiRectangle(faces[i].FaceBox).Item1;
                Point point1 = ConvertPreviewToUiRectangle(faces[i].FaceBox).Item2;

                faceBoundingBox.StrokeThickness = 4;

                faceBoundingBox.Stroke = (i == 0 ? new SolidColorBrush(Colors.Blue) : new SolidColorBrush(Colors.DeepSkyBlue));

                await TakeAdvancePhotoAsync(new BitmapBounds()
                {
                    X = faces[i].FaceBox.X - (uint)(faces[i].FaceBox.Width / 2),
                    Y = faces[i].FaceBox.Y - (uint)(faces[i].FaceBox.Height / 2),
                    Width = faces[i].FaceBox.Width * 2,
                    Height = faces[i].FaceBox.Height * 2
                });

                // Add your Face Indentify mMhod
                if (!string.IsNullOrEmpty(captureStr))
                    username = await FaceIdentify(captureStr);

                if (username == null)
                    username = "";
                faceBoundingBoxs.Add(faceBoundingBox);
                usernames.Add(username);
                points.Add(point1);
            }

            // Remove any existing rectangles from previous events
            FacesCanvas.Children.Clear();

            // For each detected face
            for (int i = 0; i < faces.Count; i++)
            {
                TextBlock text = new TextBlock()
                {
                    Width = faceBoundingBoxs[i].Width * 2,
                    Height = faceBoundingBoxs[i].Height * 2,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                    FontWeight = Windows.UI.Text.FontWeights.Bold,
                    Text = usernames[i],
                    FontSize = 30
                };

                if (usernames[i] == null) return;
                text.Text = usernames[i];
                Canvas.SetLeft(text, points[i].X);
                Canvas.SetTop(text, points[i].Y + text.Height);
                FacesCanvas.Children.Add(text);
            }

            // Update the face detection bounding box canvas orientation
            SetFacesCanvasRotation();
        }

        private async Task<string> FaceIdentify(string base64) {
            string JsonText = string.Empty;
            #region 識別人臉物件(Json格式)
            RequestIdentifyData IdentifyCustomerData = new RequestIdentifyData();
            IdentifyCustomerData.Service = "CYUT";
            IdentifyCustomerData.FaceImage = base64;
            IdentifyCustomerData.CustomerID = "";
            IdentifyCustomerData.UserID = "";
            #endregion

            // Serialize
            JsonText = JsonConvert.SerializeObject(IdentifyCustomerData);

            HttpContent Content = new StringContent($"Jsontext={JsonText}", Encoding.UTF8, "");
            var Response = await Client.PostAsync("", Content);
            if (Response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                Stream ResponseBody = await Response.Content.ReadAsStreamAsync();
                XmlTextReader Reader = new XmlTextReader(ResponseBody);
                Reader.MoveToContent();
                string FormatXML = Reader.ReadInnerXml();
                // Deserialize
                ResponseIdentifyData CustomerData = JsonConvert.DeserializeObject<ResponseIdentifyData>(FormatXML);
                if (CustomerData == null) return null;

                Debug.WriteLine(CustomerData.UserID);
                Debug.WriteLine("successs");
                return CustomerData.UserID;
            }
            else
            {
                Debug.WriteLine("False");
                return "False";
            }
        }

        /// <summary>
        /// Takes face information defined in preview coordinates and returns one in UI coordinates, taking
        /// into account the position and size of the preview control.
        /// </summary>
        /// <param name="faceBoxInPreviewCoordinates">Face coordinates as retried from the FaceBox property of a DetectedFace, in preview coordinates.</param>
        /// <returns>Rectangle in UI (CaptureElement) coordinates, to be used in a Canvas control.</returns>
        private Tuple<Rectangle, Point> ConvertPreviewToUiRectangle(BitmapBounds faceBoxInPreviewCoordinates) {
            double x = 0;
            double y = 0;
            var result = new Rectangle();
            var previewStream = _previewProperties as VideoEncodingProperties;
            Tuple<Rectangle, Point> tuple = new Tuple<Rectangle, Point>(result, new Point(x, y));

            // If there is no available information about the preview, return an empty rectangle, as re-scaling to the screen coordinates will be impossible
            if (previewStream == null) return tuple;

            // Similarly, if any of the dimensions is zero (which would only happen in an error case) return an empty rectangle
            if (previewStream.Width == 0 || previewStream.Height == 0) return tuple;

            double streamWidth = previewStream.Width;
            double streamHeight = previewStream.Height;

            // For portrait orientations, the width and height need to be swapped
            //if(_displayOrientation == DisplayOrientations.Portrait || _displayOrientation == DisplayOrientations.PortraitFlipped) {
            //    streamHeight = previewStream.Width;
            //    streamWidth = previewStream.Height;
            //}

            // Get the rectangle that is occupied by the actual video feed
            var previewInUI = GetPreviewStreamRectInControl(previewStream, PreviewControl);

            // Scale the width and height from preview stream coordinates to window coordinates
            result.Width = (faceBoxInPreviewCoordinates.Width / streamWidth) * previewInUI.Width;
            result.Height = (faceBoxInPreviewCoordinates.Height / streamHeight) * previewInUI.Height;

            // Scale the X and Y coordinates from preview stream coordinates to window coordinates
            x = (faceBoxInPreviewCoordinates.X / streamWidth) * previewInUI.Width;
            y = (faceBoxInPreviewCoordinates.Y / streamHeight) * previewInUI.Height;
            Canvas.SetLeft(result, x);
            Canvas.SetTop(result, y);
            tuple = new Tuple<Rectangle, Point>(result, new Point(x, y));
            return tuple;
        }

        /// <summary>
        /// Calculates the size and location of the rectangle that contains the preview stream within the preview control, when the scaling mode is Uniform
        /// </summary>
        /// <param name="previewResolution">The resolution at which the preview is running</param>
        /// <param name="previewControl">The control that is displaying the preview using Uniform as the scaling mode</param>
        /// <returns></returns>
        public Rect GetPreviewStreamRectInControl(VideoEncodingProperties previewResolution, CaptureElement previewControl) {
            var result = new Rect();

            // In case this function is called before everything is initialized correctly, return an empty result
            if (previewControl == null || previewControl.ActualHeight < 1 || previewControl.ActualWidth < 1 ||
                previewResolution == null || previewResolution.Height == 0 || previewResolution.Width == 0)
            {
                return result;
            }

            var streamWidth = previewResolution.Width;
            var streamHeight = previewResolution.Height;

            // For portrait orientations, the width and height need to be swapped
            if (_displayOrientation == DisplayOrientations.Portrait || _displayOrientation == DisplayOrientations.PortraitFlipped)
            {
                streamWidth = previewResolution.Height;
                streamHeight = previewResolution.Width;
            }

            // Start by assuming the preview display area in the control spans the entire width and height both (this is corrected in the next if for the necessary dimension)
            result.Width = previewControl.ActualWidth;
            result.Height = previewControl.ActualHeight;

            // If UI is "wider" than preview, letterboxing will be on the sides
            if ((previewControl.ActualWidth / previewControl.ActualHeight > streamWidth / (double)streamHeight))
            {
                var scale = previewControl.ActualHeight / streamHeight;
                var scaledWidth = streamWidth * scale;

                result.X = (previewControl.ActualWidth - scaledWidth) / 2.0;
                result.Width = scaledWidth;
            }
            else // Preview stream is "wider" than UI, so letterboxing will be on the top+bottom
            {
                var scale = previewControl.ActualWidth / streamWidth;
                var scaledHeight = streamHeight * scale;

                result.Y = (previewControl.ActualHeight - scaledHeight) / 2.0;
                result.Height = scaledHeight;
            }

            return result;
        }

        #endregion
    }
}
