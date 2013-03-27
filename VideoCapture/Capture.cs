using System;
using System.Collections.Generic;
using System.Linq;

using MonoTouch.Foundation;
using MonoTouch.UIKit;
using System.Drawing;
using MonoTouch.CoreGraphics;
using MonoTouch.CoreAnimation;
using MonoTouch.AVFoundation;
using MonoTouch.CoreVideo;
using MonoTouch.CoreMedia;
using System.IO;
using System.Threading;
using MonoTouch.AssetsLibrary;

namespace VideoCapture
{
    public class VideoCapture : AVCaptureVideoDataOutputSampleBufferDelegate
    {
        public UIImageView ImageView { get; protected set; }

        public UILabel InfoLabel { get; protected set; }

        AVCaptureSession session;
        AVAssetWriter writer;
        AVAssetWriterInput inputWriter;
        CMTime lastSampleTime;
        NSUrl videoUrl;

        public VideoCapture(UIImageView imgView, UILabel label)
        {
            this.ImageView = imgView;
            this.InfoLabel = label;
        }

        public bool StartRecording()
        {
            try
            {
                session = MaybeInitializeSession();
                if (session == null)
                {
                    Failure.Alert("Couldn't initialize session");
                    return false;
                }
                writer = MaybeInitializeAssetWriter();
                if (writer == null)
                {
                    Failure.Alert("Couldn't initialize writer");
                    return false;
                }
                inputWriter = MaybeInitializeInputWriter();
                if (inputWriter == null)
                {
                    Failure.Alert("Couldn't initialize input writer");
                    return false;
                }
                if (!writer.CanAddInput(inputWriter))
                {
                    Failure.Alert("Couldn't add input writer to writer");
                    return false;
                }
                writer.AddInput(inputWriter);

                session.StartRunning();
                return true;
            }
            catch (Exception x)
            {
                Failure.Alert(x.Message);
                return false;
            }
        }

        public void StopRecording()
        {
            try
            {
                session.StopRunning();
                writer.FinishWriting(() => MoveFinishedMovieToAlbum());
            }
            catch (Exception x)
            {
                Failure.Alert(x.Message);
            }
        }

        //Protected
        protected AVCaptureSession MaybeInitializeSession()
        {
            //Create the capture session
            var session = new AVCaptureSession()
            {
                SessionPreset = AVCaptureSession.PresetMedium
            };

            //Setup the video capture
            var captureDevice = AVCaptureDevice.DefaultDeviceWithMediaType(AVMediaType.Video);
            if (captureDevice == null)
            {
                Failure.Alert("No captureDevice - this won't work on the simulator, try a physical device");
                return null;
            }
            var input = AVCaptureDeviceInput.FromDevice(captureDevice);
            if (input == null)
            {
                Failure.Alert("No input - this won't work on the simulator, try a physical device");
                return null;
            }
            session.AddInput(input);

            // create a VideoDataOutput and add it to the sesion
            var output = new AVCaptureVideoDataOutput()
            {
                VideoSettings = new AVVideoSettings(CVPixelFormatType.CV32BGRA),
            };

            // configure the output
            var queue = new MonoTouch.CoreFoundation.DispatchQueue("myQueue");
            output.SetSampleBufferDelegate(this, queue);
            session.AddOutput(output);

            return session;
        }

        protected AVAssetWriter MaybeInitializeAssetWriter()
        {
            var filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Temporary.mov");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
            videoUrl = NSUrl.FromFilename(filePath);

            NSError error;
            var writer = new AVAssetWriter(videoUrl, AVFileType.QuickTimeMovie, out error);
            if (error != null)
            {
                Failure.Alert(error.LocalizedDescription);
                return null;
            }
            return writer;
        }

        protected AVAssetWriterInput MaybeInitializeInputWriter()
        {
            try
            {
                var dictionary = NSDictionary.FromObjectsAndKeys(
                new NSObject[] { AVVideo.CodecH264, new NSNumber(640), new NSNumber(480) },
                new NSObject[] { AVVideo.CodecKey, AVVideo.WidthKey, AVVideo.HeightKey }
                );

                var writerInput = new AVAssetWriterInput(AVMediaType.Video, dictionary);
                writerInput.ExpectsMediaDataInRealTime = true;
                return writerInput;
            }
            catch (Exception x)
            {
                Failure.Alert(x.Message);
                return null;
            }
        }

        protected void MoveFinishedMovieToAlbum()
        {
            var lib = new ALAssetsLibrary();
            lib.WriteVideoToSavedPhotosAlbum(videoUrl, (o, s) => InfoLabel.BeginInvokeOnMainThread(() => InfoLabel.Text = "Movie saved to Album."));
        }

        //Implement AVCaptureVideoDataOutputSampleBufferDelegate
        int frame = 0;
        public override void DidOutputSampleBuffer(AVCaptureOutput captureOutput, CMSampleBuffer sampleBuffer, AVCaptureConnection connection)
        {
            try
            {
                lastSampleTime = sampleBuffer.PresentationTimeStamp;

                var image = ImageFromSampleBuffer(sampleBuffer);

                if (frame == 0)
                {
                    writer.StartWriting();
                    writer.StartSessionAtSourceTime(lastSampleTime);
                    frame = 1;
                }
                String infoString = "";
                if (inputWriter.ReadyForMoreMediaData)
                {
                    if (!inputWriter.AppendSampleBuffer(sampleBuffer))
                    {
                        infoString = "Failed to append sample buffer";
                    }
                    else
                    {
                        infoString = String.Format("{0} frames captured", frame++);
                    }
                }
                else
                {
                    infoString = "Writer not ready";
                }

                ImageView.BeginInvokeOnMainThread(() => ImageView.Image = image);
                InfoLabel.BeginInvokeOnMainThread(() => InfoLabel.Text = infoString);
            }
            catch (Exception e)
            {
                Failure.Alert(e.Message);
            }
            finally
            {
                sampleBuffer.Dispose();
            }
        }

        UIImage ImageFromSampleBuffer(CMSampleBuffer sampleBuffer)
        {
            // Get the CoreVideo image
            using (var pixelBuffer = sampleBuffer.GetImageBuffer() as CVPixelBuffer)
            {
                // Lock the base address
                pixelBuffer.Lock(0);
                // Get the number of bytes per row for the pixel buffer
                var baseAddress = pixelBuffer.BaseAddress;
                int bytesPerRow = pixelBuffer.BytesPerRow;
                int width = pixelBuffer.Width;
                int height = pixelBuffer.Height;
                var flags = CGBitmapFlags.PremultipliedFirst | CGBitmapFlags.ByteOrder32Little;
                // Create a CGImage on the RGB colorspace from the configured parameter above
                using (var cs = CGColorSpace.CreateDeviceRGB())
                using (var context = new CGBitmapContext(baseAddress, width, height, 8, bytesPerRow, cs, (CGImageAlphaInfo)flags))
                using (var cgImage = context.ToImage())
                {
                    pixelBuffer.Unlock(0);
                    return UIImage.FromImage(cgImage);
                }
            }
        }
    }

    public class Failure
    {
        public static void Alert(string msg)
        {
            new NSString().InvokeOnMainThread(() => new UIAlertView("Trouble", msg, null, "OK", null).Show());
        }
    }

    public class ContentView : UIView
    {
        public UIImageView ImageView { get; protected set; }

        public UILabel InfoLabel { get; protected set; }

        public ContentView(UIColor fillColor, EventHandler recordToggle)
        {
            BackgroundColor = fillColor;

            ImageView = new UIImageView(new RectangleF(10, 10, UIScreen.MainScreen.Bounds.Width - 20, UIScreen.MainScreen.Bounds.Height - 120));
            ImageView.BackgroundColor = UIColor.Blue;

            InfoLabel = new UILabel(new RectangleF(UIScreen.MainScreen.Bounds.Width - 150, 10, 140, 50));

            var toggleButton = UIButton.FromType(UIButtonType.RoundedRect);
            toggleButton.SetTitle("Record", UIControlState.Normal);
            toggleButton.Frame = new RectangleF(
                new PointF(UIScreen.MainScreen.Bounds.Width / 2 - toggleButton.IntrinsicContentSize.Width / 2, UIScreen.MainScreen.Bounds.Height - toggleButton.IntrinsicContentSize.Height - 50), toggleButton.IntrinsicContentSize);

            toggleButton.TouchUpInside += recordToggle;

            AddSubview(ImageView);
            AddSubview(InfoLabel);
            AddSubview(toggleButton);
        }
    }

    public class VideoCaptureController : UIViewController
    {
        String title;
        UIColor color;
        protected bool recording = false;
        protected VideoCapture videoCapture;

        public VideoCaptureController(UIColor viewColor, String title)
            : base()
        {
            this.title = title;

            color = viewColor;
        }

        public override void DidReceiveMemoryWarning()
        {
            // Releases the view if it doesn't have a superview.
            base.DidReceiveMemoryWarning();
        }

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();

            Title = title;

            var cv = new ContentView(color, RecordToggle);

            View = cv;
        }

        protected void RecordToggle(Object sender, EventArgs e)
        {
            if (!recording)
            {
                ContentView cv = View as ContentView;
                videoCapture = new VideoCapture(cv.ImageView, cv.InfoLabel);
                recording = videoCapture.StartRecording();
            }
            else
            {
                videoCapture.StopRecording();
                recording = false;
            }
            var newTitle = recording ? "Stop" : "Record";
            (sender as UIButton).SetTitle(newTitle, UIControlState.Normal);
        }
    }

    [Register("AppDelegate")]
    public class AppDelegate : UIApplicationDelegate
    {
        UIWindow window;

        public override bool FinishedLaunching(UIApplication app, NSDictionary options)
        {
            var viewController = new VideoCaptureController(UIColor.Red, "Main");

            window = new UIWindow(UIScreen.MainScreen.Bounds);
            window.RootViewController = viewController;

            window.MakeKeyAndVisible();

            return true;
        }

    }

    public class Application
    {
        static void Main(string[] args)
        {
            UIApplication.Main(args, null, "AppDelegate");
        }
    }
}