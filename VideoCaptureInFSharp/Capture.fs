namespace VideoCaptureInFSharp

open System
open System.Collections.Generic
open System.Linq

open MonoTouch.Foundation
open MonoTouch.UIKit
open MonoTouch.AVFoundation
open MonoTouch.CoreMedia

open System.Drawing
open MonoTouch.CoreGraphics
open MonoTouch.CoreAnimation
open MonoTouch.CoreVideo
open System.Threading
open MonoTouch.AssetsLibrary
open System.IO

[<AutoOpen>]
module _Private =
    // SEE: http://stackoverflow.com/questions/10719770/is-there-anyway-to-use-c-sharp-implicit-operators-from-f
    let inline (!>) (x:^a) : ^b = ((^a or ^b) : (static member op_Implicit : ^a -> ^b) x) 

    let makeToggleButton (recordToggle : EventHandler) =
        let toggleButton = UIButton.FromType (UIButtonType.RoundedRect)
        toggleButton.SetTitle ("Record", UIControlState.Normal)
        toggleButton.Frame <-
            new RectangleF (
                new PointF (
                    UIScreen.MainScreen.Bounds.Width / 2.0f - toggleButton.IntrinsicContentSize.Width / 2.0f,
                    UIScreen.MainScreen.Bounds.Height - toggleButton.IntrinsicContentSize.Height - 50.0f),
                    toggleButton.IntrinsicContentSize)
        toggleButton.TouchUpInside.Add (fun e -> recordToggle.Invoke(null, e))
        toggleButton 

type Failure() =
    static member Alert (msg) =
        let obj = new NSString() :> NSObject
        let alert = new UIAlertView("Trouble", msg, null, "OK", null)
        new NSAction(fun () -> alert.Show()) |> obj.InvokeOnMainThread

type VideoCapture(imgView, label) = 
    inherit AVCaptureVideoDataOutputSampleBufferDelegate()

    member val ImageView : UIImageView = null with get, set
    member val InfoLabel : UILabel = null with get, set

    member val session : Option<AVCaptureSession> = None with get, set
    member val writer : Option<AVAssetWriter> = None with get, set
    member val inputWriter : Option<AVAssetWriterInput> = None with get, set
    member val lastSampleTime : CMTime = CMTime(0L, 0) with get, set
    member val videoUrl : NSUrl = null with get, set
    member val frame = 0 with get, set

    member x.StartRecording () =
        try
            x.session <- x.MaybeInitializeSession()
            match x.session with
            | None ->
                Failure.Alert("Couldn't initialize session")
                false
            | Some(session) ->
                x.writer <- x.MaybeInitializeAssetWriter()
                match x.writer with
                | None ->
                    Failure.Alert("Couldn't initialize writer")
                    false
                | Some(writer) ->
                    x.inputWriter <- x.MaybeInitializeInputWriter()
                    match x.inputWriter with
                    | None ->
                        Failure.Alert("Couldn't initialize input writer")
                        false
                    | Some(inputWriter) ->
                        if not (writer.CanAddInput(inputWriter)) then
                            Failure.Alert("Couldn't add input writer to writer")
                            false
                        else
                            writer.AddInput(inputWriter)
                            session.StartRunning()
                            true
        with
            | e -> Failure.Alert(e.Message); false

    member x.StopRecording () =
        try
            match x.session, x.writer with
            | Some(session), Some(writer) ->
                session.StopRunning()
                writer.FinishWriting(new NSAction(fun () -> x.MoveFinishedMovieToAlbum()))
            | _ -> ()
        with
            | e -> Failure.Alert(e.Message)

    member x.MaybeInitializeSession () =
        //Create the capture session
        let session = new AVCaptureSession(SessionPreset = AVCaptureSession.PresetMedium)

        //Setup the video capture
        let captureDevice = AVCaptureDevice.DefaultDeviceWithMediaType(!> AVMediaType.Video)
        if captureDevice = null then
            Failure.Alert("No captureDevice - this won't work on the simulator, try a physical device")
            None
        else
            let input = AVCaptureDeviceInput.FromDevice(captureDevice)
            if input = null then
                Failure.Alert("No input - this won't work on the simulator, try a physical device")
                None
            else
                session.AddInput(input)

                // create a VideoDataOutput and add it to the sesion
                let output = new AVCaptureVideoDataOutput(VideoSettings = new AVVideoSettings(CVPixelFormatType.CV32BGRA))

                // configure the output
                let queue = new MonoTouch.CoreFoundation.DispatchQueue("myQueue")
                output.SetSampleBufferDelegate(x, queue)
                session.AddOutput(output)
                Some(session)

    member x.MaybeInitializeAssetWriter () =
        let filePath =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Temporary.mov")

        if File.Exists(filePath) then File.Delete(filePath)
        
        x.videoUrl <- NSUrl.FromFilename(filePath)

        let error : Ref<NSError> = ref null
        let writer = new AVAssetWriter(x.videoUrl, !> AVFileType.QuickTimeMovie, error)
        if error.Value <> null then
            Failure.Alert(error.Value.LocalizedDescription)
            None
        else
            Some(writer)

    member __.MaybeInitializeInputWriter () =
        try
            let dictionary =
                let objects = [|AVVideo.CodecH264; new NSNumber(640); new NSNumber(480)|] : NSObject []
                let keys = [|AVVideo.CodecKey; AVVideo.WidthKey; AVVideo.HeightKey|] : NSObject []
                NSDictionary.FromObjectsAndKeys(objects, keys)

            let writerInput = new AVAssetWriterInput(!> AVMediaType.Video, dictionary)
            writerInput.ExpectsMediaDataInRealTime <- true
            Some(writerInput)
        with
            | e -> Failure.Alert(e.Message); None

    member x.MoveFinishedMovieToAlbum () =
        let lib = new ALAssetsLibrary()
        lib.WriteVideoToSavedPhotosAlbum(
            x.videoUrl,
            new ALAssetsLibraryWriteCompletionDelegate((fun _ _ ->
                x.InfoLabel.BeginInvokeOnMainThread((fun () -> x.InfoLabel.Text <- "Movie saved to Album.")))))

    member x.DidOutputSampleBuffer (captureOutput, sampleBuffer : CMSampleBuffer, connection) =
        try
            try
                x.lastSampleTime <- sampleBuffer.PresentationTimeStamp

                match x.frame, x.writer, x.inputWriter with
                | 0, Some(w), _ ->
                    w.StartWriting() |> ignore
                    w.StartSessionAtSourceTime(x.lastSampleTime)
                    x.frame <- 1
                | _, _, Some(iw) ->
                    x.ImageView.BeginInvokeOnMainThread((fun () ->
                        let image = x.ImageFromSampleBuffer(sampleBuffer)
                        x.ImageView.Image <- image))

                    x.InfoLabel.BeginInvokeOnMainThread((fun () ->
                        let infoString =
                            if iw.ReadyForMoreMediaData then
                                if not (iw.AppendSampleBuffer(sampleBuffer)) then
                                    "Failed to append sample buffer"
                                else
                                    String.Format("{0} frames captured", (x.frame + 1))
                            else
                                "Writer not ready";

                        x.InfoLabel.Text <- infoString))
                | _ -> ()
            with
                | e -> Failure.Alert(e.Message)
        finally
            sampleBuffer.Dispose()

    member __.ImageFromSampleBuffer (sampleBuffer : CMSampleBuffer) : UIImage =
        // Get the CoreVideo image
        use pixelBuffer = sampleBuffer.GetImageBuffer() :?> CVPixelBuffer
        // Lock the base address
        pixelBuffer.Lock(CVOptionFlags.None) |> ignore
        // Get the number of bytes per row for the pixel buffer
        let baseAddress = pixelBuffer.BaseAddress
        let bytesPerRow = pixelBuffer.BytesPerRow
        let width = pixelBuffer.Width
        let height = pixelBuffer.Height
        let flags = CGBitmapFlags.PremultipliedFirst ||| CGBitmapFlags.ByteOrder32Little
        // Create a CGImage on the RGB colorspace from the configured parameter above
        use cs = CGColorSpace.CreateDeviceRGB()
        use context = new CGBitmapContext(baseAddress, width, height, 8, bytesPerRow, cs, flags)
        use cgImage = context.ToImage()
        pixelBuffer.Unlock(CVOptionFlags.None) |> ignore
        UIImage.FromImage(cgImage)

type ContentView(fillColor, recordToggle : EventHandler) as x =
    inherit UIView()
    do
        x.BackgroundColor <- fillColor
        let imageBounds =
            new RectangleF (
                10.0f,
                10.0f,
                UIScreen.MainScreen.Bounds.Width - 20.0f,
                UIScreen.MainScreen.Bounds.Height - 120.0f)

        [
            new UIImageView (imageBounds, BackgroundColor = UIColor.Blue) :> UIView
            new UILabel (new RectangleF (UIScreen.MainScreen.Bounds.Width - 150.0f, 10.0f, 140.0f, 50.0f)) :> UIView
            makeToggleButton (recordToggle) :> UIView
        ]
        |> List.iter (fun v -> x.AddSubview v)

    member val ImageView : UIImageView = null with get, set
    member val InfoLabel : UILabel = null with get, set

        
type VideoCaptureController(viewColor, title) =
    inherit UIViewController()

    let cv = base.View :?> ContentView
    member val recording = false with get, set
    member val videoCapture : VideoCapture = new VideoCapture(cv.ImageView, cv.InfoLabel) with get, set

    override x.DidReceiveMemoryWarning () =
        base.DidReceiveMemoryWarning()

    override x.ViewDidLoad() =
        base.ViewDidLoad()
        x.Title <- title
        x.View <- new ContentView(viewColor, (new EventHandler((fun o e -> x.RecordToggle(o, e)))))

    member x.RecordToggle (sender : obj, e : EventArgs) =
        if not x.recording then
            x.videoCapture <- new VideoCapture(cv.ImageView, cv.InfoLabel)
            x.recording <- x.videoCapture.StartRecording()
        else
            x.videoCapture.StopRecording()
            x.recording <- false

        let newTitle = if x.recording then "Stop" else "Record"
        (sender :?> UIButton).SetTitle(newTitle, UIControlState.Normal)

[<Register("AppDelegate")>]
type AppDelegate() =
    inherit UIApplicationDelegate()

    override __.FinishedLaunching(app, options) =
        let viewController = new VideoCaptureController(UIColor.Red, "Main")
        let window = new UIWindow(UIScreen.MainScreen.Bounds)
        window.RootViewController <- viewController
        window.MakeKeyAndVisible()
        true

type Application() =
    static member Main(args) = UIApplication.Main(args, null, "AppDelegate")
