﻿namespace VideoCaptureInFSharp

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

type Failure() =
    static member Alert (msg) =
        let obj = new NSString() :> NSObject
        let alert = new UIAlertView("Trouble", msg, null, "OK", null)
        new NSAction(fun () -> alert.Show()) |> obj.InvokeOnMainThread

type Recording =
    {
        VideoUrl : NSUrl
        Session : AVCaptureSession
        Writer : AVAssetWriter
        InputWriter : AVAssetWriterInput
    }

[<AutoOpen>]
module _Private =
    // SEE: http://stackoverflow.com/questions/10719770/is-there-anyway-to-use-c-sharp-implicit-operators-from-f
    let inline (!>) (x:^a) : ^b = ((^a or ^b) : (static member op_Implicit : ^a -> ^b) x) 

    let makeToggleButton (recordToggle : EventHandler) =
        let tb = UIButton.FromType (UIButtonType.RoundedRect)
        tb.SetTitle ("Record", UIControlState.Normal)
        tb.Frame <-
            new RectangleF (
                new PointF (
                    UIScreen.MainScreen.Bounds.Width / 2.0f - tb.IntrinsicContentSize.Width / 2.0f,
                    UIScreen.MainScreen.Bounds.Height - tb.IntrinsicContentSize.Height - 50.0f),
                    tb.IntrinsicContentSize)
        tb.TouchUpInside.Add (fun e -> recordToggle.Invoke(null, e))
        tb 

    let imageFromSampleBuffer (sampleBuffer : CMSampleBuffer) =
        // Get the CoreVideo image
        use pb = sampleBuffer.GetImageBuffer() :?> CVPixelBuffer
        // Lock the base address
        pb.Lock(CVOptionFlags.None) |> ignore
        let flags = CGBitmapFlags.PremultipliedFirst ||| CGBitmapFlags.ByteOrder32Little
        // Create a CGImage on the RGB colorspace from the configured parameter above
        use cs = CGColorSpace.CreateDeviceRGB()
        use context = new CGBitmapContext(pb.BaseAddress, pb.Width, pb.Height, 8, pb.BytesPerRow, cs, flags)
        use cgImage = context.ToImage()
        pb.Unlock(CVOptionFlags.None) |> ignore
        UIImage.FromImage(cgImage)

    let initializeInputWriter () =
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

    let initializeAssetWriter () =
        let filePath =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Temporary.mov")

        if File.Exists(filePath) then File.Delete(filePath)
        let videoUrl = NSUrl.FromFilename(filePath)
        let error : Ref<NSError> = ref null
        let writer = new AVAssetWriter(videoUrl, !> AVFileType.QuickTimeMovie, error)
        if error.Value <> null then Choice2Of2(error.Value.LocalizedDescription)
        else Choice1Of2(videoUrl, writer)

    let initializeSession f =
        //Create the capture session
        let session = new AVCaptureSession(SessionPreset = AVCaptureSession.PresetMedium)

        //Setup the video capture
        let captureDevice = AVCaptureDevice.DefaultDeviceWithMediaType(!> AVMediaType.Video)
        if captureDevice = null then
            Choice2Of2("No captureDevice - this won't work on the simulator, try a physical device")
        else
            let input = AVCaptureDeviceInput.FromDevice(captureDevice)
            if input = null then
                Choice2Of2("No input - this won't work on the simulator, try a physical device")
            else
                session.AddInput(input)

                // create a VideoDataOutput and add it to the sesion
                let output = new AVCaptureVideoDataOutput(VideoSettings = new AVVideoSettings(CVPixelFormatType.CV32BGRA))

                // configure the output
                let queue = new MonoTouch.CoreFoundation.DispatchQueue("myQueue")
                output.SetSampleBufferDelegate(f, queue)
                session.AddOutput(output)
                Choice1Of2(session)

    let startRecording
        (initSession : unit -> AVCaptureSession option) 
        (initWriter : unit -> (NSUrl * AVAssetWriter) option)
        (initInputWriter : unit -> AVAssetWriterInput option) =
        try
            match initSession() with
            | None -> Choice2Of2("Couldn't initialize session")
            | Some(session : AVCaptureSession) ->
                match initWriter() with
                | None -> Choice2Of2("Couldn't initialize writer")
                | Some(url, writer : AVAssetWriter) ->
                    match initInputWriter() with
                    | None -> Choice2Of2("Couldn't initialize input writer")
                    | Some(inputWriter) ->
                        if not (writer.CanAddInput(inputWriter)) then
                            Choice2Of2("Couldn't add input writer to writer")
                        else
                            writer.AddInput(inputWriter)
                            session.StartRunning()
                            Choice1Of2(
                                {
                                    VideoUrl = url
                                    Session = session
                                    Writer = writer
                                    InputWriter = inputWriter
                                })
        with
            | e -> Choice2Of2(e.Message)

    let stopRecording onComplete = function 
        | {Session = session; Writer = writer; InputWriter = _} ->
            session.StopRunning()
            writer.FinishWriting(new NSAction(onComplete))

type LabelledView = {Label : UILabel; View : UIImageView}

type VideoCapture(labelledView) = 
    inherit AVCaptureVideoDataOutputSampleBufferDelegate()
    let frame = ref 0
    member val recording : Recording option = None with get, set

    member x.StartRecording () =
        match startRecording (x.InitializeSession) (x.InitializeAssetWriter) initializeInputWriter with
        | Choice1Of2(r) -> x.recording <- Some(r); true
        | Choice2Of2(m) -> Failure.Alert(m); x.recording <- None; false

    member x.StopRecording () =
        match x.recording with
        | Some(r) -> try stopRecording (fun () -> x.MoveFinishedMovieToAlbum()) r with | e -> Failure.Alert(e.Message)
        | None -> ()

    member x.InitializeSession () =
        match initializeSession x with
        | Choice1Of2(s) -> Some(s)
        | Choice2Of2(m) -> Failure.Alert(m); None

    member x.InitializeAssetWriter () =
        match initializeAssetWriter() with
        | Choice1Of2(u, w) -> Some(u, w)
        | Choice2Of2(m) -> Failure.Alert(m); None

    member x.MoveFinishedMovieToAlbum () =
        match x.recording, labelledView with
        | Some({VideoUrl = url; Session = _; Writer = _; InputWriter = _}), {Label = l; View = _} ->
            (new ALAssetsLibrary()).WriteVideoToSavedPhotosAlbum(
                url,
                new ALAssetsLibraryWriteCompletionDelegate((fun _ _ ->
                    l.BeginInvokeOnMainThread((fun () ->
                        l.Text <- "Movie saved to Album.")))))
        | None, _ -> ()

    member x.DidOutputSampleBuffer (captureOutput, sampleBuffer : CMSampleBuffer, connection) =
        try
            try
                let lastSampleTime = sampleBuffer.PresentationTimeStamp
                match !frame, x.recording with
                | 0, Some({Session = _; Writer = w; InputWriter = _}) ->
                    w.StartWriting() |> ignore
                    w.StartSessionAtSourceTime(lastSampleTime)
                    frame := 1

                | _, Some({Session = _; Writer = _; InputWriter = iw}) ->
                    match labelledView with
                    | {Label = l; View = v} ->
                        v.BeginInvokeOnMainThread((fun () -> v.Image <- imageFromSampleBuffer(sampleBuffer)))

                        l.BeginInvokeOnMainThread((fun () ->
                            let infoString =
                                if iw.ReadyForMoreMediaData then
                                    if not (iw.AppendSampleBuffer(sampleBuffer)) then
                                        "Failed to append sample buffer"
                                    else
                                        frame := !frame + 1
                                        String.Format("{0} frames captured", !frame)
                                else
                                    "Writer not ready"

                            l.Text <- infoString))
                | _ -> ()
            with
                | e -> Failure.Alert(e.Message)
        finally
            sampleBuffer.Dispose()

type ContentView(fillColor, recordToggle : EventHandler) as x =
    inherit UIView()

    let lv =
        let imageBounds =
            new RectangleF (
                10.0f,
                10.0f,
                UIScreen.MainScreen.Bounds.Width - 20.0f,
                UIScreen.MainScreen.Bounds.Height - 120.0f)
        {
            Label = new UILabel (new RectangleF (UIScreen.MainScreen.Bounds.Width - 150.0f, 10.0f, 140.0f, 50.0f))
            View = new UIImageView (imageBounds, BackgroundColor = UIColor.Blue)
        }

    do
        x.BackgroundColor <- fillColor
        [
            lv.View :> UIView
            lv.Label :> UIView
            makeToggleButton (recordToggle) :> UIView
        ]
        |> List.iter (fun v -> x.AddSubview v)

    member val LabelledView = lv

type VideoCapturing =
    {Capture : VideoCapture option; IsRecording : bool}
    member x.Toggle lv =
        match x with
        | {Capture = Some(c); IsRecording = true} ->
            c.StopRecording()
            {x with IsRecording = false}
        | {Capture = _; IsRecording = false} ->
            let capture = new VideoCapture(lv())
            {Capture = Some(capture); IsRecording = capture.StartRecording()}
        | {Capture = None; IsRecording = true} ->
            failwith "No capture but recording. How could that happen?"

type VideoCaptureController(viewColor, title) =
    inherit UIViewController()
    member val recordingCapture = {Capture = None; IsRecording = false} with get, set

    override x.ViewDidLoad() =
        base.ViewDidLoad()
        x.Title <- title
        x.View <- new ContentView(viewColor, (new EventHandler((fun o e -> x.RecordToggle(o, e)))))

    member x.RecordToggle (sender : obj, e : EventArgs) =
        let getLabelledView =
            match base.View with
            | :? ContentView as cv -> (fun () -> cv.LabelledView)
            | _ -> failwith "Base class is not a ContentView"

        x.recordingCapture <- x.recordingCapture.Toggle getLabelledView
        let newTitle = if x.recordingCapture.IsRecording then "Stop" else "Record"
        (sender :?> UIButton).SetTitle(newTitle, UIControlState.Normal)

[<Register("AppDelegate")>]
type AppDelegate() =
    inherit UIApplicationDelegate()

    override __.FinishedLaunching(app, options) =
        let viewController = new VideoCaptureController(UIColor.Red, "Main")
        let window = new UIWindow(UIScreen.MainScreen.Bounds, RootViewController = viewController)
        window.MakeKeyAndVisible()
        true

type Application() =
    static member Main(args) = UIApplication.Main(args, null, "AppDelegate")