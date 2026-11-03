namespace PdfButler.App

/// What a document pane currently shows.
type Centre =
    | Welcome
    | DocumentShown
    | OpenFailed of fileName: string * message: string
