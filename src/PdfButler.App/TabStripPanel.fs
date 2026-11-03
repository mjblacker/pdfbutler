/// Lays tabs out in one row: they shrink to fit, then scroll sideways by an offset (no scroll bar).
/// Sizing and scrolling rules live in Core's TabStrip module.
module PdfButler.App.TabStripPanel

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Threading
open PdfButler.Core

type TabStripPanel() as this =
    inherit Panel()

    let p = TabStrip.defaultParams
    let mutable offset = 0.0
    let mutable width = 0.0
    let mutable pendingReveal: int option = None
    let layoutChanged = Event<unit>()

    do this.ClipToBounds <- true

    member _.Params = p
    member _.Offset = offset
    member _.Overflows = TabStrip.overflows p this.Children.Count width
    member _.CanScrollBack = offset > 0.5
    member _.CanScrollForward = offset < TabStrip.extent p this.Children.Count width - width - 0.5

    /// Raised (posted, outside layout) after every arrange, so arrow buttons can follow.
    member _.LayoutChanged = layoutChanged.Publish

    member _.ScrollTo(value: float) =
        let v = TabStrip.clampOffset p this.Children.Count width value
        if abs (v - offset) > 0.01 then
            offset <- v
            this.InvalidateArrange()

    member _.ScrollBySteps(steps: float) =
        this.ScrollTo(TabStrip.scrollBy p this.Children.Count width offset steps)

    /// Scroll tab `index` into view at the next arrange (works right after adding a tab).
    member _.Reveal(index: int) =
        pendingReveal <- Some index
        this.InvalidateArrange()

    override _.MeasureOverride(available: Avalonia.Size) =
        let n = this.Children.Count
        let avail = if Double.IsInfinity available.Width then float n * (p.MaxWidth + p.Spacing) else available.Width
        let w = TabStrip.tabWidth p n avail
        let mutable height = 0.0
        for c in this.Children do
            c.Measure(Avalonia.Size(w, available.Height))
            height <- max height c.DesiredSize.Height
        Avalonia.Size(min avail (TabStrip.extent p n avail), height)

    override _.ArrangeOverride(final: Avalonia.Size) =
        width <- final.Width
        let n = this.Children.Count
        let w = TabStrip.tabWidth p n width
        offset <-
            match pendingReveal with
            | Some i -> TabStrip.reveal p n width offset i
            | None -> TabStrip.clampOffset p n width offset
        pendingReveal <- None
        for i in 0 .. n - 1 do
            this.Children[i].Arrange(Avalonia.Rect(TabStrip.tabLeft p n width i - offset, 0.0, w, final.Height))
        Dispatcher.UIThread.Post(fun () -> layoutChanged.Trigger())
        final
