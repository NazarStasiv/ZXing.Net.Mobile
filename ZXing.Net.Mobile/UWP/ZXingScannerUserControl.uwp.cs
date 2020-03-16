﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Graphics.Display;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.System.Display;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace ZXing.UI
{
	public class ZXingScannerUserControl : UserControl, IScannerView
	{
		Grid rootGrid;
		CaptureElement captureElement;
		Grid gridCustomOverlay;
		Grid gridDefaultOverlay;
		TextBlock bottomText;

		public BarcodeScanningOptions Options { get; }

		public BarcodeScannerOverlay<UIElement> Overlay { get; }

		public event EventHandler<BarcodeScannedEventArgs> OnBarcodeScanned;

		public bool IsAnalyzing { get; set; }

		public bool IsTorchOn
			=> HasTorch && mediaCapture.VideoDeviceController.TorchControl.Enabled;

		public Task TorchAsync(bool on)
		{
			if (HasTorch)
				mediaCapture.VideoDeviceController.TorchControl.Enabled = on;
			return Task.CompletedTask;
		}

		public async Task ToggleTorchAsync()
		{
			if (HasTorch)
				await TorchAsync(!IsTorchOn);
		}

		public async Task AutoFocusAsync()
			=> await AutoFocusAsync(0, 0, false);

		public async Task AutoFocusAsync(int x, int y)
			=> await AutoFocusAsync(x, y, true);

		public ZXingScannerUserControl(BarcodeScanningOptions options = null, BarcodeScannerOverlay<UIElement> overlay = null)
		{
			Options = options ?? new BarcodeScanningOptions();
			Overlay = overlay;

			rootGrid = new Grid { VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch };

			captureElement = new CaptureElement { Stretch = Stretch.UniformToFill };
			rootGrid.Children.Add(captureElement);
			
			gridCustomOverlay = new Grid
			{
				Visibility = Visibility.Collapsed,
				HorizontalAlignment = HorizontalAlignment.Stretch,
				VerticalAlignment = VerticalAlignment.Stretch
			};
			rootGrid.Children.Add(gridCustomOverlay);

			gridDefaultOverlay = new Grid
			{
				HorizontalAlignment = HorizontalAlignment.Stretch,
				VerticalAlignment = VerticalAlignment.Stretch
			};
			gridDefaultOverlay.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			gridDefaultOverlay.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			gridDefaultOverlay.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

			var topRect = new Rectangle
			{
				HorizontalAlignment = HorizontalAlignment.Stretch,
				VerticalAlignment = VerticalAlignment.Stretch,
				Fill = new SolidColorBrush(Colors.Black),
				Opacity = 0.3
			};
			Grid.SetRow(topRect, 0);
			gridDefaultOverlay.Children.Add(topRect);

			var topBorder = new Border
			{
				HorizontalAlignment = HorizontalAlignment.Stretch,
				VerticalAlignment = VerticalAlignment.Stretch,
			};
			Grid.SetRow(topBorder, 0);

			var topText = new TextBlock
			{
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				TextAlignment = TextAlignment.Center,
				TextWrapping = TextWrapping.WrapWholeWords,
				Foreground = new SolidColorBrush(Colors.White),
				Text = overlay?.TopText ?? string.Empty
			};
			topBorder.Child = topText;
			gridDefaultOverlay.Children.Add(topBorder);

			var lineRect = new Rectangle
			{
				VerticalAlignment = VerticalAlignment.Center,
				HorizontalAlignment = HorizontalAlignment.Stretch,
				Fill = new SolidColorBrush(Colors.Red),
				Height = 4,
				Opacity = 0.5
			};
			Grid.SetRow(lineRect, 1);
			gridDefaultOverlay.Children.Add(lineRect);
			var line = new Line
			{
				VerticalAlignment = VerticalAlignment.Center,
				HorizontalAlignment = HorizontalAlignment.Stretch,
				Fill = new SolidColorBrush(Colors.Red),
				Height = 4,
				StrokeThickness = 4,
				Stroke = new SolidColorBrush(Colors.Red)
			};
			Grid.SetRow(line, 1);
			gridDefaultOverlay.Children.Add(line);

			var botRect = new Rectangle
			{
				HorizontalAlignment = HorizontalAlignment.Stretch,
				VerticalAlignment = VerticalAlignment.Stretch,
				Fill = new SolidColorBrush(Colors.Black),
				Opacity = 0.3
			};
			Grid.SetRow(botRect, 2);
			gridDefaultOverlay.Children.Add(botRect);

			var botBorder = new Border
			{
				HorizontalAlignment = HorizontalAlignment.Stretch,
				VerticalAlignment = VerticalAlignment.Stretch,
			};
			Grid.SetRow(botBorder, 2);

			bottomText = new TextBlock
			{
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				TextAlignment = TextAlignment.Center,
				TextWrapping = TextWrapping.WrapWholeWords,
				Foreground = new SolidColorBrush(Colors.White),
				Text = overlay?.BottomText ?? string.Empty
			};
			botBorder.Child = bottomText;
			gridDefaultOverlay.Children.Add(botBorder);

			rootGrid.Children.Add(gridDefaultOverlay);

			Content = rootGrid;

			displayOrientation = displayInformation.CurrentOrientation;
			displayInformation.OrientationChanged += DisplayInformation_OrientationChanged;

			StartScanningAsync();
		}

		async void DisplayInformation_OrientationChanged(DisplayInformation sender, object args)
		{
			//This safeguards against a null reference if the device is rotated *before* the first call to StartScanning
			if (mediaCapture == null)
				return;

			displayOrientation = sender.CurrentOrientation;
			var props = mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);
			await SetPreviewRotationAsync(props);
		}

		// Receive notifications about rotation of the UI and apply any necessary rotation to the preview stream
		readonly DisplayInformation displayInformation = DisplayInformation.GetForCurrentView();
		DisplayOrientations displayOrientation = DisplayOrientations.Portrait;
		VideoFrame videoFrame;

		// Information about the camera device.
		bool mirroringPreview = false;

		// Rotation metadata to apply to the preview stream (MF_MT_VIDEO_ROTATION)
		// Reference: http://msdn.microsoft.com/en-us/library/windows/apps/xaml/hh868174.aspx
		static readonly Guid rotationKey = new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");

		// Prevent the screen from sleeping while the camera is running
		readonly DisplayRequest displayRequest = new DisplayRequest();

		// For listening to media property changes
		readonly SystemMediaTransportControls systemMediaControls = SystemMediaTransportControls.GetForCurrentView();


		async Task StartScanningAsync()
		{
			if (stopping)
			{
				Logger.Warn("Camera is already closing");
				return;
			}


			displayRequest.RequestActive();

			IsAnalyzing = true;

			if (Overlay?.CustomOverlay != null)
			{
				gridCustomOverlay.Children.Clear();
				gridCustomOverlay.Children.Add(Overlay.CustomOverlay);

				gridCustomOverlay.Visibility = Visibility.Visible;
				gridDefaultOverlay.Visibility = Visibility.Collapsed;
			}
			else
			{
				gridCustomOverlay.Visibility = Visibility.Collapsed;
				gridDefaultOverlay.Visibility = Visibility.Visible;
			}

			// Find which device to use
			var preferredCamera = await GetFilteredCameraOrDefaultAsync(Options);
			if (preferredCamera == null)
			{
				var error = "No camera available";
				Logger.Error(error);
				isMediaCaptureInitialized = false;
				return;
			}

			if (preferredCamera.EnclosureLocation == null || preferredCamera.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Unknown)
			{
				// No information on the location of the camera, assume it's an external camera, not integrated on the device.
				//externalCamera = true;
			}
			else
			{
				// Camera is fixed on the device.
				//externalCamera = false;

				// Only mirror the preview if the camera is on the front panel.
				mirroringPreview = preferredCamera.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Front;
			}

			mediaCapture = new MediaCapture();

			// Initialize the capture with the settings above
			try
			{
				await mediaCapture.InitializeAsync(new MediaCaptureInitializationSettings
				{
					StreamingCaptureMode = StreamingCaptureMode.Video,
					VideoDeviceId = preferredCamera.Id
				});
				isMediaCaptureInitialized = true;
			}
			catch (UnauthorizedAccessException)
			{
				Logger.Error("Denied access to the camera");
			}
			catch (Exception ex)
			{
				Logger.Error(ex, "Exception when init MediaCapture");
			}

			if (!isMediaCaptureInitialized)
			{
				Logger.Error("Unexpected error on Camera initialisation");
				return;
			}


			// Set the capture element's source to show it in the UI
			captureElement.Source = mediaCapture;
			captureElement.FlowDirection = mirroringPreview ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

			try
			{
				// Start the preview
				await mediaCapture.StartPreviewAsync();
			}
			catch (Exception ex)
			{
				Logger.Error(ex, "Unexpected error on Camera initialisation");
				return;
			}

			if (mediaCapture.CameraStreamState == CameraStreamState.Streaming)
				Logger.Info("Camera Initialized and streaming");

			// Get all the available resolutions for preview
			var availableProperties = mediaCapture.VideoDeviceController.GetAvailableMediaStreamProperties(MediaStreamType.VideoPreview);
			var availableResolutions = new List<CameraResolution>();
			foreach (var ap in availableProperties)
			{
				var vp = (VideoEncodingProperties)ap;
				Logger.Info($"Camera Preview Resolution: {vp.Width}x{vp.Height}");
				availableResolutions.Add(new CameraResolution { Width = (int)vp.Width, Height = (int)vp.Height });
			}
			var previewResolution =  Options?.CameraResolutionSelector?.Invoke(availableResolutions);

			if (availableResolutions == null || availableResolutions.Count < 1)
			{
				Logger.Error("Camera is busy. Try to close all applications that use camera.");
				return;
			}

			// If the user did not specify a resolution, let's try and find a suitable one
			if (previewResolution == null)
			{
				// Loop through all supported sizes
				foreach (var sps in availableResolutions)
				{
					// Find one that's >= 640x360 but <= 1000x1000
					// This will likely pick the *smallest* size in that range, which should be fine
					if (sps.Width >= 640 && sps.Width <= 1000 && sps.Height >= 360 && sps.Height <= 1000)
					{
						previewResolution = new CameraResolution
						{
							Width = sps.Width,
							Height = sps.Height
						};
						break;
					}
				}
			}

			if (previewResolution == null)
				previewResolution = availableResolutions.LastOrDefault();

			if (previewResolution == null)
			{
				Logger.Info("No preview resolution available. Camera may be in use by another application.");
				return;
			}

			Logger.Info($"Using Preview Resolution: {previewResolution.Width}x{previewResolution.Height}");

			// Find the matching property based on the selection, again
			var chosenProp = availableProperties.FirstOrDefault(ap => ((VideoEncodingProperties)ap).Width == previewResolution.Width && ((VideoEncodingProperties)ap).Height == previewResolution.Height);

			// Pass in the requested preview size properties
			// so we can set them at the same time as the preview rotation
			// to save an additional set property call
			await SetPreviewRotationAsync(chosenProp);

			// *after* the preview is setup, set this so that the UI layout happens
			// otherwise the preview gets stuck in a funny place on screen
			captureElement.Stretch = Stretch.UniformToFill;

			await SetupAutoFocus();

			var zxing = Options.BuildBarcodeReader();

			timerPreview = new Timer(async (state) =>
			{

				var delay = Options.DelayBetweenAnalyzingFrames;

				if (stopping || processing || !IsAnalyzing
				|| (mediaCapture == null || mediaCapture.CameraStreamState != Windows.Media.Devices.CameraStreamState.Streaming))
				{
					timerPreview.Change(delay, Timeout.Infinite);
					return;
				}

				processing = true;

				SoftwareBitmapLuminanceSource luminanceSource = null;

				try
				{
					// Get preview 
					var frame = await mediaCapture.GetPreviewFrameAsync(videoFrame);

					// Create our luminance source
					luminanceSource = new SoftwareBitmapLuminanceSource(frame.SoftwareBitmap);

				}
				catch (Exception ex)
				{
					Logger.Error(ex, "GetPreviewFrame Failed: {0}");
				}

				ZXing.Result[] results = null;

				try
				{
					// Try decoding the image
					if (luminanceSource != null)
					{
						results = Options?.ScanMultiple ?? false
							? zxing.DecodeMultiple(luminanceSource)
							: new[] { zxing.Decode(luminanceSource) };
					}
				}
				catch (Exception ex)
				{
					Logger.Warn(ex, "Warning: zxing.Decode Failed");
				}

				// Check if a result was found
				if (results != null && results.Length > 0)
				{
					var filteredResults = results.Where(r => r != null && !string.IsNullOrWhiteSpace(r.Text)).ToArray();

					if (filteredResults.Any())
					{
						delay = Options.DelayBetweenContinuousScans;

						OnBarcodeScanned?.Invoke(this, new BarcodeScannedEventArgs(filteredResults));
					}
				}

				processing = false;

				timerPreview.Change(delay, Timeout.Infinite);

			}, null, Options.InitialDelayBeforeAnalyzingFrames, Timeout.Infinite);
		}

		async Task<DeviceInformation> GetFilteredCameraOrDefaultAsync(BarcodeScanningOptions options)
		{
			var videoCaptureDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

			var useFront = options.UseFrontCameraIfAvailable.HasValue && options.UseFrontCameraIfAvailable.Value;

			var selectedCamera = videoCaptureDevices.FirstOrDefault(vcd => vcd.EnclosureLocation != null
				&& ((!useFront && vcd.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Back)
					|| (useFront && vcd.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Front)));


			// we fall back to the first camera that we can find.  
			if (selectedCamera == null)
			{
				var whichCamera = useFront ? "front" : "back";
				Logger.Info("Finding " + whichCamera + " camera failed, opening first available camera");
				selectedCamera = videoCaptureDevices.FirstOrDefault();
			}

			return selectedCamera;
		}

		protected override void OnPointerPressed(PointerRoutedEventArgs e)
		{
			Logger.Info("AutoFocus requested");
			base.OnPointerPressed(e);
			var pt = e.GetCurrentPoint(captureElement);
			new Task(async () => { await AutoFocusAsync((int)pt.Position.X, (int)pt.Position.Y, true); });
		}

		Timer timerPreview;
		MediaCapture mediaCapture;

		bool stopping = false;
		bool isMediaCaptureInitialized = false;

		volatile bool processing = false;

		public bool IsFocusSupported
			=> mediaCapture != null
					&& isMediaCaptureInitialized
					&& mediaCapture.VideoDeviceController != null
					&& mediaCapture.VideoDeviceController.FocusControl != null
					&& mediaCapture.VideoDeviceController.FocusControl.Supported;

		async Task SetupAutoFocus()
		{
			if (IsFocusSupported)
			{
				var focusControl = mediaCapture.VideoDeviceController.FocusControl;

				var focusSettings = new FocusSettings();

				if (Options.DisableAutofocus)
				{
					focusSettings.Mode = FocusMode.Manual;
					focusSettings.Distance = ManualFocusDistance.Nearest;
					focusControl.Configure(focusSettings);
					return;
				}

				focusSettings.AutoFocusRange = focusControl.SupportedFocusRanges.Contains(AutoFocusRange.FullRange)
					? AutoFocusRange.FullRange
					: focusControl.SupportedFocusRanges.FirstOrDefault();

				var supportedFocusModes = focusControl.SupportedFocusModes;
				if (supportedFocusModes.Contains(FocusMode.Continuous))
				{
					focusSettings.Mode = FocusMode.Continuous;
				}
				else if (supportedFocusModes.Contains(FocusMode.Auto))
				{
					focusSettings.Mode = FocusMode.Auto;
				}

				if (focusSettings.Mode == FocusMode.Continuous || focusSettings.Mode == FocusMode.Auto)
				{
					focusSettings.WaitForFocus = false;
					focusControl.Configure(focusSettings);
					await focusControl.FocusAsync();
				}
			}
		}

		public bool HasTorch
			=> mediaCapture != null
					&& mediaCapture.VideoDeviceController != null
					&& mediaCapture.VideoDeviceController.TorchControl != null
					&& mediaCapture.VideoDeviceController.TorchControl.Supported;

		public async Task AutoFocusAsync(int x, int y, bool useCoordinates)
		{
			if (Options.DisableAutofocus)
				return;

			if (IsFocusSupported && mediaCapture?.CameraStreamState == CameraStreamState.Streaming)
			{
				var focusControl = mediaCapture.VideoDeviceController.FocusControl;
				var roiControl = mediaCapture.VideoDeviceController.RegionsOfInterestControl;
				try
				{
					if (roiControl.AutoFocusSupported && roiControl.MaxRegions > 0)
					{
						if (useCoordinates)
						{
							var props = mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);

							var previewEncodingProperties = GetPreviewResolution(props);
							var previewRect = GetPreviewStreamRectInControl(previewEncodingProperties, captureElement);
							var focusPreview = ConvertUiTapToPreviewRect(new Point(x, y), new Size(20, 20), previewRect);
							var regionOfInterest = new RegionOfInterest
							{
								AutoFocusEnabled = true,
								BoundsNormalized = true,
								Bounds = focusPreview,
								Type = RegionOfInterestType.Unknown,
								Weight = 100
							};
							await roiControl.SetRegionsAsync(new[] { regionOfInterest }, true);

							var focusRange = focusControl.SupportedFocusRanges.Contains(AutoFocusRange.FullRange)
								? AutoFocusRange.FullRange
								: focusControl.SupportedFocusRanges.FirstOrDefault();

							var focusMode = focusControl.SupportedFocusModes.Contains(FocusMode.Single)
								? FocusMode.Single
								: focusControl.SupportedFocusModes.FirstOrDefault();

							var settings = new FocusSettings
							{
								Mode = focusMode,
								AutoFocusRange = focusRange,
							};

							focusControl.Configure(settings);
						}
						else
						{
							// If no region provided, clear any regions and reset focus
							await roiControl.ClearRegionsAsync();
						}
					}

					await focusControl.FocusAsync();
				}
				catch (Exception ex)
				{
					Logger.Error(ex, "AutoFocusAsync Error");
				}
			}
		}

		async Task StopAsync()
		{
			if (stopping)
				return;

			stopping = true;
			IsAnalyzing = false;

			try
			{
				displayRequest?.RequestRelease();
			}
			catch (Exception ex)
			{
				Logger.Error(ex, "Release Request Failed");
			}

			try
			{
				if (IsTorchOn)
					await TorchAsync(false);
				if (isMediaCaptureInitialized)
					await mediaCapture.StopPreviewAsync();
				if (Overlay?.CustomOverlay != null)
					gridCustomOverlay.Children.Remove(Overlay.CustomOverlay);
			}
			catch { }
			finally
			{
				//second execution from sample will crash if the object is not properly disposed (always on mobile, sometimes on desktop)
				if (mediaCapture != null)
					mediaCapture.Dispose();
			}

			//this solves a crash occuring when the user rotates the screen after the QR scanning is closed
			displayInformation.OrientationChanged -= DisplayInformation_OrientationChanged;

			if (timerPreview != null)
				timerPreview.Change(Timeout.Infinite, Timeout.Infinite);
			stopping = false;
		}

		public async void Dispose()
		{
			await StopAsync();
			gridCustomOverlay?.Children?.Clear();
		}

		protected override void OnTapped(TappedRoutedEventArgs e)
			=> base.OnTapped(e);

		//void ButtonToggleFlash_Click(object sender, RoutedEventArgs e)
		//	=> ToggleTorch();

		/// <summary>
		/// Gets the current orientation of the UI in relation to the device and applies a corrective rotation to the preview
		/// </summary>
		async Task SetPreviewRotationAsync(IMediaEncodingProperties props)
		{
			// Only need to update the orientation if the camera is mounted on the device.
			if (mediaCapture == null)
				return;

			// Calculate which way and how far to rotate the preview.
			CalculatePreviewRotation(out var sourceRotation, out var rotationDegrees);

			// Set preview rotation in the preview source.
			mediaCapture.SetPreviewRotation(sourceRotation);

			// Add rotation metadata to the preview stream to make sure the aspect ratio / dimensions match when rendering and getting preview frames
			//var props = mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);
			props.Properties.Add(rotationKey, rotationDegrees);
			await mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoPreview, props);

			var currentPreviewResolution = GetPreviewResolution(props);
			// Setup a frame to use as the input settings
			videoFrame = new VideoFrame(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, (int)currentPreviewResolution.Width, (int)currentPreviewResolution.Height);
		}

		Size GetPreviewResolution(IMediaEncodingProperties props)
		{
			// Get our preview properties
			if (props is VideoEncodingProperties previewProperties)
			{
				var streamWidth = previewProperties.Width;
				var streamHeight = previewProperties.Height;

				// For portrait orientations, the width and height need to be swapped
				if (displayOrientation == DisplayOrientations.Portrait || displayOrientation == DisplayOrientations.PortraitFlipped)
				{
					streamWidth = previewProperties.Height;
					streamHeight = previewProperties.Width;
				}

				return new Size(streamWidth, streamHeight);
			}

			return default;
		}

		/// <summary>
		/// Reads the current orientation of the app and calculates the VideoRotation necessary to ensure the preview is rendered in the correct orientation.
		/// </summary>
		/// <param name="sourceRotation">The rotation value to use in MediaCapture.SetPreviewRotation.</param>
		/// <param name="rotationDegrees">The accompanying rotation metadata with which to tag the preview stream.</param>
		void CalculatePreviewRotation(out VideoRotation sourceRotation, out int rotationDegrees)
		{
			// Note that in some cases, the rotation direction needs to be inverted if the preview is being mirrored.
			switch (displayInformation.CurrentOrientation)
			{
				case DisplayOrientations.Portrait:
					if (mirroringPreview)
					{
						rotationDegrees = 270;
						sourceRotation = VideoRotation.Clockwise270Degrees;
					}
					else
					{
						rotationDegrees = 90;
						sourceRotation = VideoRotation.Clockwise90Degrees;
					}
					break;

				case DisplayOrientations.LandscapeFlipped:
					// No need to invert this rotation, as rotating 180 degrees is the same either way.
					rotationDegrees = 180;
					sourceRotation = VideoRotation.Clockwise180Degrees;
					break;

				case DisplayOrientations.PortraitFlipped:
					if (mirroringPreview)
					{
						rotationDegrees = 90;
						sourceRotation = VideoRotation.Clockwise90Degrees;
					}
					else
					{
						rotationDegrees = 270;
						sourceRotation = VideoRotation.Clockwise270Degrees;
					}
					break;

				case DisplayOrientations.Landscape:
				default:
					rotationDegrees = 0;
					sourceRotation = VideoRotation.None;
					break;
			}
		}

		/// <summary>
		/// Applies the necessary rotation to a tap on a CaptureElement (with Stretch mode set to Uniform) to account for device orientation
		/// </summary>
		/// <param name="tap">The location, in UI coordinates, of the user tap</param>
		/// <param name="size">The size, in UI coordinates, of the desired focus rectangle</param>
		/// <param name="previewRect">The area within the CaptureElement that is actively showing the preview, and is not part of the letterboxed area</param>
		/// <returns>A Rect that can be passed to the MediaCapture Focus and RegionsOfInterest APIs, with normalized bounds in the orientation of the native stream</returns>
		Rect ConvertUiTapToPreviewRect(Point tap, Size size, Rect previewRect)
		{
			// Adjust for the resulting focus rectangle to be centered around the position
			double left = tap.X - size.Width / 2, top = tap.Y - size.Height / 2;

			// Get the information about the active preview area within the CaptureElement (in case it's letterboxed)
			double previewWidth = previewRect.Width, previewHeight = previewRect.Height;
			double previewLeft = previewRect.Left, previewTop = previewRect.Top;

			// Transform the left and top of the tap to account for rotation
			switch (displayOrientation)
			{
				case DisplayOrientations.Portrait:
					var tempLeft = left;

					left = top;
					top = previewRect.Width - tempLeft;
					break;
				case DisplayOrientations.LandscapeFlipped:
					left = previewRect.Width - left;
					top = previewRect.Height - top;
					break;
				case DisplayOrientations.PortraitFlipped:
					var tempTop = top;

					top = left;
					left = previewRect.Width - tempTop;
					break;
			}

			// For portrait orientations, the information about the active preview area needs to be rotated
			if (displayOrientation == DisplayOrientations.Portrait || displayOrientation == DisplayOrientations.PortraitFlipped)
			{
				previewWidth = previewRect.Height;
				previewHeight = previewRect.Width;
				previewLeft = previewRect.Top;
				previewTop = previewRect.Left;
			}

			// Normalize width and height of the focus rectangle
			var width = size.Width / previewWidth;
			var height = size.Height / previewHeight;

			// Shift rect left and top to be relative to just the active preview area
			left -= previewLeft;
			top -= previewTop;

			// Normalize left and top
			left /= previewWidth;
			top /= previewHeight;

			// Ensure rectangle is fully contained within the active preview area horizontally
			left = Math.Max(left, 0);
			left = Math.Min(1 - width, left);

			// Ensure rectangle is fully contained within the active preview area vertically
			top = Math.Max(top, 0);
			top = Math.Min(1 - height, top);

			// Create and return resulting rectangle
			return new Rect(left, top, width, height);
		}

		/// <summary>
		/// Calculates the size and location of the rectangle that contains the preview stream within the preview control, when the scaling mode is Uniform
		/// </summary>
		/// <param name="previewResolution">The resolution at which the preview is running</param>
		/// <param name="previewControl">The control that is displaying the preview using Uniform as the scaling mode</param>
		/// <returns></returns>
		static Rect GetPreviewStreamRectInControl(Size previewResolution, CaptureElement previewControl)
		{
			var result = new Rect();

			// In case this function is called before everything is initialized correctly, return an empty result
			if (previewControl == null || previewControl.ActualHeight < 1 || previewControl.ActualWidth < 1 ||
				previewResolution.Height < 1 || previewResolution.Width < 1)
			{
				return result;
			}

			var streamWidth = previewResolution.Width;
			var streamHeight = previewResolution.Height;

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
	}
}