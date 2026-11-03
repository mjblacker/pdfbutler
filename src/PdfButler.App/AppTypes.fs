namespace PdfButler.App

/// What a document pane currently shows.
type Centre =
    | Welcome
    | Opening of fileName: string
    | DocumentShown
    | OpenFailed of fileName: string * message: string
