/// What an action may ask for besides a file (AUTOMATION-1636, S1/S2 of ADR-059 §35): an
/// output another action produced, named by an `OutputLabel`, and a typed question about the
/// environment, an `OracleQuestion`. Both are parsed once here, so a key built from them
/// never holds free text.
namespace FsHotWatch.Core

open System

/// Why text is not an environment variable name.
[<RequireQualifiedAccess>]
type EnvVarNameError =
    /// Null or empty.
    | Empty
    /// Contains '=', which ends a name in an environment block.
    | ContainsEquals
    /// Contains a control character.
    | ControlCharacter

    override this.ToString() =
        match this with
        | Empty -> "an environment variable name cannot be empty"
        | ContainsEquals -> "an environment variable name cannot contain '='"
        | ControlCharacter -> "an environment variable name cannot contain control characters"

/// The name of an environment variable, as given (case is kept: it matters on Unix).
[<Struct>]
type EnvVarName =
    private
    | EnvVarName of string

    /// The name.
    member this.Text =
        let (EnvVarName text) = this
        text

    override this.ToString() = this.Text

/// Parses environment variable names.
[<RequireQualifiedAccess>]
module EnvVarName =
    /// `text` as an environment variable name, or the first rule it breaks.
    let parse (text: string) : Result<EnvVarName, EnvVarNameError> =
        if String.IsNullOrEmpty text then
            Error EnvVarNameError.Empty
        elif text.Contains '=' then
            Error EnvVarNameError.ContainsEquals
        elif Seq.exists Char.IsControl text then
            Error EnvVarNameError.ControlCharacter
        else
            Ok(EnvVarName text)

/// Why text is not an output label.
[<RequireQualifiedAccess>]
type OutputLabelError =
    /// Null or empty.
    | Empty
    /// The character at this index is not one of a-z, 0-9, '-' or '/'.
    | InvalidCharacter of index: int

    override this.ToString() =
        match this with
        | Empty -> "an output label cannot be empty"
        | InvalidCharacter index ->
            "an output label is a-z, 0-9, '-' and '/'; character "
            + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " is not"

/// The name of an output another action produced, which this one consumes: for example
/// `check/fcs-signature`. Lowercase letters, digits, '-' and '/' only, so it is one spelling
/// and can never hold a path, a tab or a newline.
[<Struct>]
type OutputLabel =
    private
    | OutputLabel of string

    /// The label.
    member this.Text =
        let (OutputLabel text) = this
        text

    override this.ToString() = this.Text

/// Parses output labels.
[<RequireQualifiedAccess>]
module OutputLabel =
    let private allowed (c: char) =
        (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-' || c = '/'

    /// `text` as an output label, or where it breaks the rule.
    let parse (text: string) : Result<OutputLabel, OutputLabelError> =
        if String.IsNullOrEmpty text then
            Error OutputLabelError.Empty
        else
            match Seq.tryFindIndex (allowed >> not) text with
            | Some index -> Error(OutputLabelError.InvalidCharacter index)
            | None -> Ok(OutputLabel text)

/// A question about the environment an action may need answered (S2). CLOSED: a new
/// question is a new case, and every place that answers questions must then answer it.
/// Each is asked again on every run; the answer's digest, never the answer, enters a key.
[<RequireQualifiedAccess>]
type OracleQuestion =
    /// Which .NET SDK a `dotnet` started here resolves, and from where.
    | DotnetSdk
    /// The value of one environment variable, or that it is unset.
    | Variable of name: EnvVarName
    /// The repository's .NET tool manifest (`.config/dotnet-tools.json`), or that it has none.
    | ToolManifest
    /// The operating system and architecture (a .NET runtime identifier, e.g. `osx-arm64`).
    | Platform

    /// The question's subject in a key: one per question, so two answers to it collide.
    member this.Subject =
        match this with
        | DotnetSdk -> "dotnet-sdk"
        | Variable name -> "env:" + name.Text
        | ToolManifest -> "tool-manifest"
        | Platform -> "platform"
