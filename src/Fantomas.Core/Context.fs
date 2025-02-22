module internal Fantomas.Core.Context

open System
open FSharp.Compiler.Text
open FSharp.Compiler.Syntax
open Fantomas.Core
open Fantomas.Core.ISourceTextExtensions
open Fantomas.Core.FormatConfig
open Fantomas.Core.TriviaTypes

type WriterEvent =
    | Write of string
    | WriteLine
    | WriteLineInsideStringConst
    | WriteBeforeNewline of string
    | WriteLineBecauseOfTrivia
    | WriteLineInsideTrivia
    | IndentBy of int
    | UnIndentBy of int
    | SetIndent of int
    | RestoreIndent of int
    | SetAtColumn of int
    | RestoreAtColumn of int

let private (|CommentOrDefineEvent|_|) we =
    match we with
    | Write w when (String.startsWithOrdinal "//" w) -> Some we
    | Write w when (String.startsWithOrdinal "#if" w) -> Some we
    | Write w when (String.startsWithOrdinal "#else" w) -> Some we
    | Write w when (String.startsWithOrdinal "#endif" w) -> Some we
    | Write w when (String.startsWithOrdinal "(*" w) -> Some we
    | _ -> None

type ShortExpressionInfo =
    { MaxWidth: int
      StartColumn: int
      ConfirmedMultiline: bool }

    member x.IsTooLong maxPageWidth currentColumn =
        currentColumn - x.StartColumn > x.MaxWidth // expression is not too long according to MaxWidth
        || (currentColumn > maxPageWidth) // expression at current position is not going over the page width

type Size =
    | CharacterWidth of maxWidth: Num
    | NumberOfItems of items: Num * maxItems: Num

type WriteModelMode =
    | Standard
    | Dummy
    | ShortExpression of ShortExpressionInfo list

type WriterModel =
    {
        /// lines of resulting text, in reverse order (to allow more efficient adding line to end)
        Lines: string list
        /// current indentation
        Indent: int
        /// helper indentation information, if AtColumn > Indent after NewLine, Indent will be set to AtColumn
        AtColumn: int
        /// text to be written before next newline
        WriteBeforeNewline: string
        /// dummy = "fake" writer used in `autoNln`, `autoNlnByFuture`
        Mode: WriteModelMode
        /// current length of last line of output
        Column: int
    }

    member __.IsDummy =
        match __.Mode with
        | Dummy -> true
        | _ -> false

module WriterModel =
    let init =
        { Lines = [ "" ]
          Indent = 0
          AtColumn = 0
          WriteBeforeNewline = ""
          Mode = Standard
          Column = 0 }

    let update maxPageWidth cmd m =
        let doNewline m =
            let m = { m with Indent = max m.Indent m.AtColumn }
            let nextLine = String.replicate m.Indent " "
            let currentLine = String.Concat(List.head m.Lines, m.WriteBeforeNewline).TrimEnd()
            let otherLines = List.tail m.Lines

            { m with
                Lines = nextLine :: currentLine :: otherLines
                WriteBeforeNewline = ""
                Column = m.Indent }

        let updateCmd cmd =
            match cmd with
            | WriteLine
            | WriteLineBecauseOfTrivia -> doNewline m
            | WriteLineInsideStringConst ->
                { m with
                    Lines = String.empty :: m.Lines
                    Column = 0 }
            | WriteLineInsideTrivia ->
                let lines =
                    match m.Lines with
                    | [] -> [ String.empty ]
                    | h :: tail -> String.empty :: (h.TrimEnd()) :: tail

                { m with Lines = lines; Column = 0 }
            | Write s ->
                { m with
                    Lines = (List.head m.Lines + s) :: (List.tail m.Lines)
                    Column = m.Column + (String.length s) }
            | WriteBeforeNewline s -> { m with WriteBeforeNewline = s }
            | IndentBy x ->
                { m with
                    Indent =
                        if m.AtColumn >= m.Indent + x then
                            m.AtColumn + x
                        else
                            m.Indent + x }
            | UnIndentBy x -> { m with Indent = max m.AtColumn <| m.Indent - x }
            | SetAtColumn c -> { m with AtColumn = c }
            | RestoreAtColumn c -> { m with AtColumn = c }
            | SetIndent c -> { m with Indent = c }
            | RestoreIndent c -> { m with Indent = c }

        match m.Mode with
        | Dummy
        | Standard -> updateCmd cmd
        | ShortExpression infos when (List.exists (fun info -> info.ConfirmedMultiline) infos) -> m
        | ShortExpression infos ->
            let nextCmdCausesMultiline =
                match cmd with
                | WriteLine
                | WriteLineBecauseOfTrivia -> true
                | WriteLineInsideStringConst -> true
                | Write _ when (String.isNotNullOrEmpty m.WriteBeforeNewline) -> true
                | _ -> false

            let updatedInfos =
                infos
                |> List.map (fun info ->
                    let tooLong = info.IsTooLong maxPageWidth m.Column

                    { info with ConfirmedMultiline = tooLong || nextCmdCausesMultiline })

            if List.exists (fun i -> i.ConfirmedMultiline) updatedInfos then
                { m with Mode = ShortExpression(updatedInfos) }
            else
                updateCmd cmd

module WriterEvents =
    let normalize ev =
        match ev with
        | Write s when s.Contains("\n") ->
            let writeLine =
                match ev with
                | CommentOrDefineEvent _ -> WriteLineInsideTrivia
                | _ -> WriteLineInsideStringConst

            // Trustworthy multiline string in the original AST can contain \r
            // Internally we process everything with \n and at the end we respect the .editorconfig end_of_line setting.
            s.Replace("\r", "").Split('\n')
            |> Seq.map (fun x -> [ Write x ])
            |> Seq.reduce (fun x y -> x @ [ writeLine ] @ y)
            |> Seq.toList
        | _ -> [ ev ]

    let isMultiline evs =
        evs
        |> Queue.toSeq
        |> Seq.exists (function
            | WriteLine
            | WriteLineBecauseOfTrivia -> true
            | _ -> false)

type internal Context =
    { Config: FormatConfig
      WriterModel: WriterModel
      WriterEvents: Queue<WriterEvent>
      TriviaBefore: Map<FsAstType, TriviaInstruction list>
      TriviaAfter: Map<FsAstType, TriviaInstruction list>
      SourceText: ISourceText option }

    /// Initialize with a string writer and use space as delimiter
    static member Default =
        { Config = FormatConfig.Default
          WriterModel = WriterModel.init
          WriterEvents = Queue.empty
          TriviaBefore = Map.empty
          TriviaAfter = Map.empty
          SourceText = None }

    static member Create
        config
        (source: ISourceText option)
        (ast: ParsedInput)
        (selection: TriviaForSelection option)
        : Context =
        let triviaInstructions, sourceText =
            match source with
            | Some source when not config.StrictMode -> Trivia.collectTrivia config source ast selection, Some source
            | _ -> [], None

        let triviaBefore, triviaAfter =
            let triviaInstructionsBefore, triviaInstructionsAfter =
                List.partition (fun ti -> ti.AddBefore) triviaInstructions

            let createMapByType = List.groupBy (fun t -> t.Type) >> Map.ofList
            createMapByType triviaInstructionsBefore, createMapByType triviaInstructionsAfter

        { Context.Default with
            Config = config
            SourceText = sourceText
            TriviaBefore = triviaBefore
            TriviaAfter = triviaAfter }

    member x.WithDummy(writerCommands, ?keepPageWidth) =
        let keepPageWidth = keepPageWidth |> Option.defaultValue false

        let mkModel m =
            { m with
                Mode = Dummy
                Lines = [ String.replicate x.WriterModel.Column " " ]
                WriteBeforeNewline = "" }
        // Use infinite column width to encounter worst-case scenario
        let config =
            { x.Config with
                MaxLineLength =
                    if keepPageWidth then
                        x.Config.MaxLineLength
                    else
                        Int32.MaxValue }

        { x with
            WriterModel = mkModel x.WriterModel
            WriterEvents = writerCommands
            Config = config }

    member x.WithShortExpression(maxWidth, ?startColumn) =
        let info =
            { MaxWidth = maxWidth
              StartColumn = Option.defaultValue x.WriterModel.Column startColumn
              ConfirmedMultiline = false }

        match x.WriterModel.Mode with
        | ShortExpression infos ->
            if List.exists (fun i -> i = info) infos then
                x
            else
                { x with WriterModel = { x.WriterModel with Mode = ShortExpression(info :: infos) } }
        | _ -> { x with WriterModel = { x.WriterModel with Mode = ShortExpression([ info ]) } }

    member x.FromSourceText(range: range) : string option =
        Option.map (fun (sourceText: ISourceText) -> sourceText.GetContentAt range) x.SourceText

    member x.HasContentAfter(``type``: FsAstType, range: range) : bool =
        match Map.tryFindOrEmptyList ``type`` x.TriviaAfter with
        | [] -> false
        | triviaInstructions -> List.exists (fun ti -> RangeHelpers.rangeEq ti.Range range) triviaInstructions

    member x.HasContentBefore(``type``: FsAstType, range: range) : bool =
        match Map.tryFindOrEmptyList ``type`` x.TriviaBefore with
        | [] -> false
        | triviaInstructions -> List.exists (fun ti -> RangeHelpers.rangeEq ti.Range range) triviaInstructions

let writerEvent e ctx =
    let evs = WriterEvents.normalize e

    let ctx' =
        { ctx with
            WriterEvents = Queue.append ctx.WriterEvents evs
            WriterModel =
                (ctx.WriterModel, evs)
                ||> Seq.fold (fun m e -> WriterModel.update ctx.Config.MaxLineLength e m) }

    ctx'

let hasWriteBeforeNewlineContent ctx =
    String.isNotNullOrEmpty ctx.WriterModel.WriteBeforeNewline

let finalizeWriterModel (ctx: Context) =
    if hasWriteBeforeNewlineContent ctx then
        writerEvent (Write ctx.WriterModel.WriteBeforeNewline) ctx
    else
        ctx

let dump (isSelection: bool) (ctx: Context) =
    let ctx = finalizeWriterModel ctx

    match ctx.WriterModel.Lines with
    | [] -> []
    | h :: tail ->
        // Always trim the last line
        h.TrimEnd() :: tail
    |> List.rev
    |> fun lines ->
        // Don't skip leading newlines when formatting a selection.
        if isSelection then lines else List.skipWhile ((=) "") lines
    |> String.concat ctx.Config.EndOfLine.NewLineString

let dumpAndContinue (ctx: Context) =
#if DEBUG
    let m = finalizeWriterModel ctx
    let lines = m.WriterModel.Lines |> List.rev

    let code = String.concat ctx.Config.EndOfLine.NewLineString lines

    printfn "%s" code
#endif
    ctx

type Context with

    member x.Column = x.WriterModel.Column
    member x.FinalizeModel = finalizeWriterModel x

let writeEventsOnLastLine ctx =
    ctx.WriterEvents
    |> Queue.rev
    |> Seq.takeWhile (function
        | WriteLine
        | WriteLineBecauseOfTrivia
        | WriteLineInsideStringConst -> false
        | _ -> true)
    |> Seq.choose (function
        | Write w when (String.length w > 0) -> Some w
        | _ -> None)

let lastWriteEventIsNewline ctx =
    ctx.WriterEvents
    |> Queue.rev
    |> Seq.skipWhile (function
        | RestoreIndent _
        | RestoreAtColumn _
        | UnIndentBy _
        | Write "" -> true
        | _ -> false)
    |> Seq.tryHead
    |> Option.map (function
        | WriteLineBecauseOfTrivia
        | WriteLine -> true
        | _ -> false)
    |> Option.defaultValue false

let private (|EmptyHashDefineBlock|_|) (events: WriterEvent array) =
    match Array.tryHead events, Array.tryLast events with
    | Some (CommentOrDefineEvent _), Some (CommentOrDefineEvent _) ->
        // Check if there is an empty block between hash defines
        // Example:
        // #if FOO
        //
        // #endif
        let emptyLinesInBetween =
            Array.forall
                (function
                | WriteLineInsideStringConst
                | Write "" -> true
                | _ -> false)
                events.[1 .. (events.Length - 2)]

        if emptyLinesInBetween then Some events else None
    | _ -> None

/// Validate if there is a complete blank line between the last write event and the last event
let newlineBetweenLastWriteEvent ctx =
    ctx.WriterEvents
    |> Queue.rev
    |> Seq.takeWhile (function
        | Write ""
        | WriteLine
        | IndentBy _
        | UnIndentBy _
        | SetIndent _
        | RestoreIndent _
        | SetAtColumn _
        | RestoreAtColumn _ -> true
        | _ -> false)
    |> Seq.filter (function
        | WriteLine _ -> true
        | _ -> false)
    |> Seq.length
    |> fun writeLines -> writeLines > 1

let lastWriteEventOnLastLine ctx =
    writeEventsOnLastLine ctx |> Seq.tryHead

let forallCharsOnLastLine f ctx =
    writeEventsOnLastLine ctx |> Seq.collect id |> Seq.forall f

// A few utility functions from https://github.com/fsharp/powerpack/blob/master/src/FSharp.Compiler.CodeDom/generator.fs

/// Indent one more level based on configuration
let indent (ctx: Context) =
    // if atColumn is bigger then after indent, then we use atColumn as base for indent
    writerEvent (IndentBy ctx.Config.IndentSize) ctx

/// Unindent one more level based on configuration
let unindent (ctx: Context) =
    writerEvent (UnIndentBy ctx.Config.IndentSize) ctx

/// Increase indent by i spaces
let incrIndent i (ctx: Context) = writerEvent (IndentBy i) ctx

/// Decrease indent by i spaces
let decrIndent i (ctx: Context) = writerEvent (UnIndentBy i) ctx

/// Apply function f at an absolute indent level (use with care)
let atIndentLevel alsoSetIndent level (f: Context -> Context) (ctx: Context) =
    if level < 0 then
        invalidArg "level" "The indent level cannot be negative."

    let m = ctx.WriterModel
    let oldIndent = m.Indent
    let oldColumn = m.AtColumn

    (writerEvent (SetAtColumn level)
     >> if alsoSetIndent then writerEvent (SetIndent level) else id
     >> f
     >> writerEvent (RestoreAtColumn oldColumn)
     >> writerEvent (RestoreIndent oldIndent))
        ctx

/// Set minimal indentation (`atColumn`) at current column position - next newline will be indented on `max indent atColumn`
/// Example:
/// { X = // indent=0, atColumn=2
///     "some long string" // indent=4, atColumn=2
///   Y = 1 // indent=0, atColumn=2
/// }
/// `atCurrentColumn` was called on `X`, then `indent` was called, but "some long string" have indent only 4, because it is bigger than `atColumn` (2).
let atCurrentColumn (f: _ -> Context) (ctx: Context) = atIndentLevel false ctx.Column f ctx

/// Like atCurrentColumn, but use current column after applying prependF
let atCurrentColumnWithPrepend (prependF: _ -> Context) (f: _ -> Context) (ctx: Context) =
    let col = ctx.Column
    (prependF >> atIndentLevel false col f) ctx

/// Write everything at current column indentation, set `indent` and `atColumn` on current column position
/// /// Example (same as above):
/// { X = // indent=2, atColumn=2
///       "some long string" // indent=6, atColumn=2
///   Y = 1 // indent=2, atColumn=2
/// }
/// `atCurrentColumn` was called on `X`, then `indent` was called, "some long string" have indent 6, because it is indented from `atCurrentColumn` pos (2).
let atCurrentColumnIndent (f: _ -> Context) (ctx: Context) = atIndentLevel true ctx.Column f ctx

/// Function composition operator
let (+>) (ctx: Context -> Context) (f: _ -> Context) x =
    let y = ctx x

    match y.WriterModel.Mode with
    | ShortExpression infos when infos |> Seq.exists (fun x -> x.ConfirmedMultiline) -> y
    | _ -> f y

let (!-) (str: string) = writerEvent (Write str)

let (!+~) (str: string) c =
    let addNewline ctx =
        not (forallCharsOnLastLine Char.IsWhiteSpace ctx)

    let c = if addNewline c then writerEvent WriteLine c else c
    writerEvent (Write str) c

/// Print object converted to string
let str (o: 'T) (ctx: Context) =
    ctx |> writerEvent (Write(o.ToString()))

/// Similar to col, and supply index as well
let coli f' (c: seq<'T>) f (ctx: Context) =
    let mutable tryPick = true
    let mutable st = ctx
    let mutable i = 0
    let e = c.GetEnumerator()

    while (e.MoveNext()) do
        if tryPick then tryPick <- false else st <- f' st

        st <- f i e.Current st
        i <- i + 1

    st

/// Similar to coli, and supply index as well to f'
let colii f' (c: seq<'T>) f (ctx: Context) =
    let mutable tryPick = true
    let mutable st = ctx
    let mutable i = 0
    let e = c.GetEnumerator()

    while (e.MoveNext()) do
        if tryPick then tryPick <- false else st <- f' i st

        st <- f i e.Current st
        i <- i + 1

    st

/// Process collection - keeps context through the whole processing
/// calls f for every element in sequence and f' between every two elements
/// as a separator. This is a variant that works on typed collections.
let col f' (c: seq<'T>) f (ctx: Context) =
    let mutable tryPick = true
    let mutable st = ctx
    let e = c.GetEnumerator()

    while (e.MoveNext()) do
        if tryPick then tryPick <- false else st <- f' st
        st <- f e.Current st

    st

// Similar to col but pass the item of 'T to f' as well
let colEx f' (c: seq<'T>) f (ctx: Context) =
    let mutable tryPick = true
    let mutable st = ctx
    let e = c.GetEnumerator()

    while (e.MoveNext()) do
        if tryPick then tryPick <- false else st <- f' e.Current st
        st <- f e.Current st

    st

/// Similar to col, apply one more function f2 at the end if the input sequence is not empty
let colPost f2 f1 (c: seq<'T>) f (ctx: Context) =
    if Seq.isEmpty c then ctx else f2 (col f1 c f ctx)

/// Similar to col, apply one more function f2 at the beginning if the input sequence is not empty
let colPre f2 f1 (c: seq<'T>) f (ctx: Context) =
    if Seq.isEmpty c then ctx else col f1 c f (f2 ctx)

let colPreEx f2 f1 (c: seq<'T>) f (ctx: Context) =
    if Seq.isEmpty c then ctx else colEx f1 c f (f2 ctx)

/// Similar to col, but apply two more functions fStart, fEnd at the beginning and the end if the input sequence is bigger thn one item
let colSurr fStart fEnd f1 (c: list<'T>) f (ctx: Context) =
    if Seq.isEmpty c then
        ctx
    else
        (col f1 c f |> fun g -> if (List.moreThanOne c) then fStart +> g +> fEnd else g) ctx

/// If there is a value, apply f and f' accordingly, otherwise do nothing
let opt (f': Context -> _) o f (ctx: Context) =
    match o with
    | Some x -> f' (f x ctx)
    | None -> ctx

/// similar to opt, only takes a single function f to apply when there is a value
let optSingle f o ctx =
    match o with
    | Some x -> f x ctx
    | None -> ctx

/// Similar to opt, but apply f2 at the beginning if there is a value
let optPre (f2: _ -> Context) (f1: Context -> _) o f (ctx: Context) =
    match o with
    | Some x -> f1 (f x (f2 ctx))
    | None -> ctx

let getListOrArrayExprSize ctx maxWidth xs =
    match ctx.Config.ArrayOrListMultilineFormatter with
    | MultilineFormatterType.CharacterWidth -> Size.CharacterWidth maxWidth
    | MultilineFormatterType.NumberOfItems -> Size.NumberOfItems(List.length xs, ctx.Config.MaxArrayOrListNumberOfItems)

let getRecordSize ctx fields =
    match ctx.Config.RecordMultilineFormatter with
    | MultilineFormatterType.CharacterWidth -> Size.CharacterWidth ctx.Config.MaxRecordWidth
    | MultilineFormatterType.NumberOfItems -> Size.NumberOfItems(List.length fields, ctx.Config.MaxRecordNumberOfItems)

/// b is true, apply f1 otherwise apply f2
let ifElse b (f1: Context -> Context) f2 (ctx: Context) = if b then f1 ctx else f2 ctx

let ifElseCtx cond (f1: Context -> Context) f2 (ctx: Context) = if cond ctx then f1 ctx else f2 ctx

let ifRagnarokElse = ifElseCtx (fun ctx -> ctx.Config.ExperimentalStroustrupStyle)

let ifRagnarok (f1: Context -> Context) =
    ifElseCtx (fun ctx -> ctx.Config.ExperimentalStroustrupStyle) f1 id

/// apply f only when cond is true
let onlyIf cond f ctx = if cond then f ctx else ctx

let onlyIfCtx cond f ctx = if cond ctx then f ctx else ctx

let onlyIfNot cond f ctx = if cond then ctx else f ctx

let whenShortIndent f ctx =
    onlyIf (ctx.Config.IndentSize < 3) f ctx

/// Repeat application of a function n times
let rep n (f: Context -> Context) (ctx: Context) =
    [ 1..n ] |> List.fold (fun c _ -> f c) ctx

// Separator functions
let sepNone = id
let sepDot = !- "."

let sepSpace (ctx: Context) =
    if ctx.WriterModel.IsDummy then
        (!- " ") ctx
    else
        match lastWriteEventOnLastLine ctx with
        | Some w when (w.EndsWith(" ") || w.EndsWith Environment.NewLine) -> ctx
        | None -> ctx
        | _ -> (!- " ") ctx

// add actual spaces until the target column is reached, regardless of previous content
// use with care
let addFixedSpaces (targetColumn: int) (ctx: Context) : Context =
    let delta = targetColumn - ctx.Column
    onlyIf (delta > 0) (rep delta (!- " ")) ctx

let sepNln = writerEvent WriteLine

// Use a different WriteLine event to indicate that the newline was introduces due to trivia
// This is later useful when checking if an expression was multiline when checking for ColMultilineItem
let sepNlnForTrivia = writerEvent WriteLineBecauseOfTrivia

let sepNlnUnlessLastEventIsNewline (ctx: Context) =
    if lastWriteEventIsNewline ctx then ctx else sepNln ctx

let sepNlnUnlessLastEventIsNewlineOrRagnarok (ctx: Context) =
    if lastWriteEventIsNewline ctx || ctx.Config.ExperimentalStroustrupStyle then
        ctx
    else
        sepNln ctx

let sepStar = sepSpace +> !- "* "
let sepStarFixed = !- "* "
let sepEq = !- " ="
let sepEqFixed = !- "="
let sepArrow = !- " -> "
let sepArrowFixed = !- "->"
let sepArrowRev = !- " <- "
let sepWild = !- "_"

let sepBar = !- "| "

/// opening token of list
let sepOpenL (ctx: Context) =
    if ctx.Config.SpaceAroundDelimiter then
        str "[ " ctx
    else
        str "[" ctx

/// closing token of list
let sepCloseL (ctx: Context) =
    if ctx.Config.SpaceAroundDelimiter then
        str " ]" ctx
    else
        str "]" ctx

/// opening token of list
let sepOpenLFixed = !- "["

/// closing token of list
let sepCloseLFixed = !- "]"

/// opening token of array
let sepOpenA (ctx: Context) =
    if ctx.Config.SpaceAroundDelimiter then
        str "[| " ctx
    else
        str "[|" ctx

/// closing token of array
let sepCloseA (ctx: Context) =
    if ctx.Config.SpaceAroundDelimiter then
        str " |]" ctx
    else
        str "|]" ctx

/// opening token of list
let sepOpenAFixed = !- "[|"
/// closing token of list
let sepCloseAFixed = !- "|]"

/// opening token of sequence or record
let sepOpenS (ctx: Context) =
    if ctx.Config.SpaceAroundDelimiter then
        str "{ " ctx
    else
        str "{" ctx

/// closing token of sequence or record
let sepCloseS (ctx: Context) =
    if ctx.Config.SpaceAroundDelimiter then
        str " }" ctx
    else
        str "}" ctx

/// opening token of anon record
let sepOpenAnonRecd (ctx: Context) =
    if ctx.Config.SpaceAroundDelimiter then
        str "{| " ctx
    else
        str "{|" ctx

/// closing token of anon record
let sepCloseAnonRecd (ctx: Context) =
    if ctx.Config.SpaceAroundDelimiter then
        str " |}" ctx
    else
        str "|}" ctx

/// opening token of anon record
let sepOpenAnonRecdFixed = !- "{|"

/// closing token of anon record
let sepCloseAnonRecdFixed = !- "|}"

/// opening token of sequence
let sepOpenSFixed = !- "{"

/// closing token of sequence
let sepCloseSFixed = !- "}"

/// opening token of tuple
let sepOpenT = !- "("

/// closing token of tuple
let sepCloseT = !- ")"

let wordAnd = sepSpace +> !- "and "
let wordAndFixed = !- "and"
let wordOr = sepSpace +> !- "or "
let wordOf = sepSpace +> !- "of "

let indentSepNlnUnindent f = indent +> sepNln +> f +> unindent

// we need to make sure each expression in the function application has offset at least greater than
// indentation of the function expression itself
// we replace sepSpace in such case
// remarks: https://github.com/fsprojects/fantomas/issues/1611
let indentIfNeeded f (ctx: Context) =
    let savedColumn = ctx.WriterModel.AtColumn

    if savedColumn >= ctx.Column then
        // missingSpaces needs to be at least one more than the column
        // of function expression being applied upon, otherwise (as known up to F# 4.7)
        // this would lead to a compile error for the function application
        let missingSpaces = (savedColumn - ctx.FinalizeModel.Column) + ctx.Config.IndentSize
        atIndentLevel true savedColumn (!-(String.replicate missingSpaces " ")) ctx
    else
        f ctx

let eventsWithoutMultilineWrite ctx =
    { ctx with
        WriterEvents =
            ctx.WriterEvents
            |> Queue.toSeq
            |> Seq.filter (function
                | Write s when s.Contains("\n") -> false
                | _ -> true)
            |> Queue.ofSeq }

let private shortExpressionWithFallback
    (shortExpression: Context -> Context)
    fallbackExpression
    maxWidth
    startColumn
    (ctx: Context)
    =
    // if the context is already inside a ShortExpression mode and tries to figure out if the expression will go over the page width,
    // we should try the shortExpression in this case.
    match ctx.WriterModel.Mode with
    | ShortExpression infos when
        (List.exists (fun info -> info.ConfirmedMultiline || info.IsTooLong ctx.Config.MaxLineLength ctx.Column) infos)
        ->
        ctx
    | _ ->
        // create special context that will process the writer events slightly different
        let shortExpressionContext =
            match startColumn with
            | Some sc -> ctx.WithShortExpression(maxWidth, sc)
            | None -> ctx.WithShortExpression(maxWidth)

        let resultContext = shortExpression shortExpressionContext

        match resultContext.WriterModel.Mode with
        | ShortExpression infos ->
            // verify the expression is not longer than allowed
            if
                List.exists
                    (fun info ->
                        info.ConfirmedMultiline
                        || info.IsTooLong ctx.Config.MaxLineLength resultContext.Column)
                    infos
            then
                fallbackExpression ctx
            else
                { resultContext with WriterModel = { resultContext.WriterModel with Mode = ctx.WriterModel.Mode } }
        | _ ->
            // you should never hit this branch
            fallbackExpression ctx

let isShortExpression maxWidth (shortExpression: Context -> Context) fallbackExpression (ctx: Context) =
    shortExpressionWithFallback shortExpression fallbackExpression maxWidth None ctx

let isShortExpressionOrAddIndentAndNewline maxWidth expr (ctx: Context) =
    shortExpressionWithFallback expr (indentSepNlnUnindent expr) maxWidth None ctx

let sepSpaceIfShortExpressionOrAddIndentAndNewline maxWidth expr (ctx: Context) =
    shortExpressionWithFallback (sepSpace +> expr) (indentSepNlnUnindent expr) maxWidth None ctx

let expressionFitsOnRestOfLine expression fallbackExpression (ctx: Context) =
    shortExpressionWithFallback expression fallbackExpression ctx.Config.MaxLineLength (Some 0) ctx

let isSmallExpression size (smallExpression: Context -> Context) fallbackExpression (ctx: Context) =
    match size with
    | CharacterWidth maxWidth -> isShortExpression maxWidth smallExpression fallbackExpression ctx
    | NumberOfItems (items, maxItems) ->
        if items > maxItems then
            fallbackExpression ctx
        else
            expressionFitsOnRestOfLine smallExpression fallbackExpression ctx

/// provide the line and column before and after the leadingExpression to to the continuation expression
let leadingExpressionResult leadingExpression continuationExpression (ctx: Context) =
    let lineCountBefore, columnBefore =
        List.length ctx.WriterModel.Lines, ctx.WriterModel.Column

    let contextAfterLeading = leadingExpression ctx

    let lineCountAfter, columnAfter =
        List.length contextAfterLeading.WriterModel.Lines, contextAfterLeading.WriterModel.Column

    continuationExpression ((lineCountBefore, columnBefore), (lineCountAfter, columnAfter)) contextAfterLeading

/// combines two expression and let the second expression know if the first expression was longer than a given threshold.
let leadingExpressionLong threshold leadingExpression continuationExpression (ctx: Context) =
    let lineCountBefore, columnBefore =
        List.length ctx.WriterModel.Lines, ctx.WriterModel.Column

    let contextAfterLeading = leadingExpression ctx

    let lineCountAfter, columnAfter =
        List.length contextAfterLeading.WriterModel.Lines, contextAfterLeading.WriterModel.Column

    continuationExpression
        (lineCountAfter > lineCountBefore || (columnAfter - columnBefore > threshold))
        contextAfterLeading

/// A leading expression is not consider multiline if it has a comment before it.
/// For example
/// let a = 7
/// // foo
/// let b = 8
/// let c = 9
/// The second binding b is not consider multiline.
let leadingExpressionIsMultiline leadingExpression continuationExpression (ctx: Context) =
    let eventCountBeforeExpression = Queue.length ctx.WriterEvents
    let contextAfterLeading = leadingExpression ctx

    let hasWriteLineEventsAfterExpression =
        contextAfterLeading.WriterEvents
        |> Queue.skipExists
            eventCountBeforeExpression
            (function
            | WriteLine _ -> true
            | _ -> false)
            (fun e ->
                match e with
                | [| CommentOrDefineEvent _ |]
                | [| WriteLine |]
                | [| Write "" |]
                | EmptyHashDefineBlock _ -> true
                | _ -> false)

    continuationExpression hasWriteLineEventsAfterExpression contextAfterLeading

let private expressionExceedsPageWidth beforeShort afterShort beforeLong afterLong expr (ctx: Context) =
    // if the context is already inside a ShortExpression mode, we should try the shortExpression in this case.
    match ctx.WriterModel.Mode with
    | ShortExpression infos when
        (List.exists (fun info -> info.ConfirmedMultiline || info.IsTooLong ctx.Config.MaxLineLength ctx.Column) infos)
        ->
        ctx
    | ShortExpression _ ->
        // if the context is already inside a ShortExpression mode, we should try the shortExpression in this case.
        (beforeShort +> expr +> afterShort) ctx
    | _ ->
        let shortExpressionContext = ctx.WithShortExpression(ctx.Config.MaxLineLength, 0)

        let resultContext = (beforeShort +> expr +> afterShort) shortExpressionContext

        let fallbackExpression = beforeLong +> expr +> afterLong

        match resultContext.WriterModel.Mode with
        | ShortExpression infos ->
            // verify the expression is not longer than allowed
            if
                List.exists
                    (fun info ->
                        info.ConfirmedMultiline
                        || info.IsTooLong ctx.Config.MaxLineLength resultContext.Column)
                    infos
            then
                fallbackExpression ctx
            else
                { resultContext with WriterModel = { resultContext.WriterModel with Mode = ctx.WriterModel.Mode } }
        | _ ->
            // you should never hit this branch
            fallbackExpression ctx

/// try and write the expression on the remainder of the current line
/// add an indent and newline if the expression is longer
let autoIndentAndNlnIfExpressionExceedsPageWidth expr (ctx: Context) =
    expressionExceedsPageWidth
        sepNone
        sepNone // before and after for short expressions
        (indent +> sepNln)
        unindent // before and after for long expressions
        expr
        ctx

let sepSpaceOrIndentAndNlnIfExpressionExceedsPageWidth expr (ctx: Context) =
    expressionExceedsPageWidth
        sepSpace
        sepNone // before and after for short expressions
        (indent +> sepNln)
        unindent // before and after for long expressions
        expr
        ctx

let sepSpaceOrDoubleIndentAndNlnIfExpressionExceedsPageWidth expr (ctx: Context) =
    expressionExceedsPageWidth
        sepSpace
        sepNone // before and after for short expressions
        (indent +> indent +> sepNln)
        (unindent +> unindent) // before and after for long expressions
        expr
        ctx

let sepSpaceWhenOrIndentAndNlnIfExpressionExceedsPageWidth (addSpace: Context -> bool) expr (ctx: Context) =
    expressionExceedsPageWidth
        (ifElseCtx addSpace sepSpace sepNone)
        sepNone // before and after for short expressions
        (indent +> sepNln)
        unindent // before and after for long expressions
        expr
        ctx

let autoNlnIfExpressionExceedsPageWidth expr (ctx: Context) =
    expressionExceedsPageWidth
        sepNone
        sepNone // before and after for short expressions
        sepNln
        sepNone // before and after for long expressions
        expr
        ctx

let autoParenthesisIfExpressionExceedsPageWidth expr (ctx: Context) =
    expressionFitsOnRestOfLine expr (sepOpenT +> expr +> sepCloseT) ctx

let futureNlnCheckMem (f, ctx) =
    if ctx.WriterModel.IsDummy then
        (false, false)
    else
        // Create a dummy context to evaluate length of current operation
        let dummyCtx: Context = ctx.WithDummy(Queue.empty, keepPageWidth = true) |> f
        WriterEvents.isMultiline dummyCtx.WriterEvents, dummyCtx.Column > ctx.Config.MaxLineLength

let futureNlnCheck f (ctx: Context) =
    let isMultiLine, isLong = futureNlnCheckMem (f, ctx)
    isMultiLine || isLong

/// similar to futureNlnCheck but validates whether the expression is going over the max page width
/// This functions is does not use any caching
let exceedsWidth maxWidth f (ctx: Context) =
    let dummyCtx: Context = ctx.WithDummy(Queue.empty, keepPageWidth = true)

    let currentLines = dummyCtx.WriterModel.Lines.Length
    let currentColumn = dummyCtx.Column
    let ctxAfter: Context = f dummyCtx
    let linesAfter = ctxAfter.WriterModel.Lines.Length
    let columnAfter = ctxAfter.Column

    linesAfter > currentLines
    || (columnAfter - currentColumn) > maxWidth
    || currentColumn > ctx.Config.MaxLineLength

/// Similar to col, skip auto newline for index 0
let colAutoNlnSkip0i f' (c: seq<'T>) f (ctx: Context) =
    coli
        f'
        c
        (fun i c ->
            if i = 0 then
                f i c
            else
                autoNlnIfExpressionExceedsPageWidth (f i c))
        ctx

/// Similar to col, skip auto newline for index 0
let colAutoNlnSkip0 f' c f = colAutoNlnSkip0i f' c (fun _ -> f)

let sepSpaceBeforeClassConstructor ctx =
    if ctx.Config.SpaceBeforeClassConstructor then
        sepSpace ctx
    else
        ctx

let sepColon (ctx: Context) =
    let defaultExpr = if ctx.Config.SpaceBeforeColon then str " : " else str ": "

    if ctx.WriterModel.IsDummy then
        defaultExpr ctx
    else
        match lastWriteEventOnLastLine ctx with
        | Some w when (w.EndsWith(" ")) -> str ": " ctx
        | None -> str ": " ctx
        | _ -> defaultExpr ctx

let sepColonFixed = !- ":"

let sepColonWithSpacesFixed = !- " : "

let sepComma (ctx: Context) =
    if ctx.Config.SpaceAfterComma then
        str ", " ctx
    else
        str "," ctx

let sepCommaFixed = str ","

let sepSemi (ctx: Context) =
    let { Config = { SpaceBeforeSemicolon = before
                     SpaceAfterSemicolon = after } } =
        ctx

    match before, after with
    | false, false -> str ";"
    | true, false -> str " ;"
    | false, true -> str "; "
    | true, true -> str " ; "
    <| ctx

let ifAlignBrackets f g =
    ifElseCtx (fun ctx -> ctx.Config.MultilineBlockBracketsOnSameColumn) f g

let printTriviaContent (c: TriviaContent) (ctx: Context) =
    let currentLastLine = ctx.WriterModel.Lines |> List.tryHead

    // Some items like #if or Newline should be printed on a newline
    // It is hard to always get this right in CodePrinter, so we detect it based on the current code.
    let addNewline =
        currentLastLine
        |> Option.map (fun line -> line.Trim().Length > 0)
        |> Option.defaultValue false

    let addSpace =
        currentLastLine
        |> Option.bind (fun line -> Seq.tryLast line |> Option.map (fun lastChar -> lastChar <> ' '))
        |> Option.defaultValue false

    match c with
    | Comment (LineCommentAfterSourceCode s) ->
        let comment = sprintf "%s%s" (if addSpace then " " else String.empty) s

        writerEvent (WriteBeforeNewline comment)
    | Comment (BlockComment (s, before, after)) ->
        ifElse (before && addNewline) sepNlnForTrivia sepNone
        +> sepSpace
        +> !-s
        +> sepSpace
        +> ifElse after sepNlnForTrivia sepNone
    | Newline -> (ifElse addNewline (sepNlnForTrivia +> sepNlnForTrivia) sepNlnForTrivia)
    | Directive s
    | Comment (CommentOnSingleLine s) -> (ifElse addNewline sepNlnForTrivia sepNone) +> !-s +> sepNlnForTrivia
    <| ctx

let printTriviaInstructions (triviaInstructions: TriviaInstruction list) =
    col sepNone triviaInstructions (fun { Trivia = trivia } -> printTriviaContent trivia.Item)

let private findTriviaOnStartFromRange nodes (range: Range) =
    nodes |> List.tryFind (fun n -> RangeHelpers.rangeStartEq n.Range range)

let enterNodeFor (mainNodeName: FsAstType) (range: Range) (ctx: Context) =
    match Map.tryFind mainNodeName ctx.TriviaBefore with
    | Some triviaNodes ->
        let triviaInstructions =
            List.filter (fun ({ Range = r }: TriviaInstruction) -> RangeHelpers.rangeEq r range) triviaNodes

        match triviaInstructions with
        | [] -> ctx
        | triviaInstructions -> printTriviaInstructions triviaInstructions ctx
    | None -> ctx

let leaveNodeFor (mainNodeName: FsAstType) (range: Range) (ctx: Context) =
    match Map.tryFind mainNodeName ctx.TriviaAfter with
    | Some triviaNodes ->
        let triviaInstructions =
            List.filter (fun ({ Range = r }: TriviaInstruction) -> RangeHelpers.rangeEq r range) triviaNodes

        match triviaInstructions with
        | [] -> ctx
        | triviaInstructions -> printTriviaInstructions triviaInstructions ctx
    | None -> ctx

let private sepConsideringTriviaContentBeforeBy
    (hasTriviaBefore: Context -> range -> bool)
    (sepF: Context -> Context)
    (range: Range)
    (ctx: Context)
    =
    if hasTriviaBefore ctx range then ctx else sepF ctx

let sepConsideringTriviaContentBeforeForMainNode sepF (mainNodeName: FsAstType) (range: Range) (ctx: Context) =
    let findNode ctx range =
        Map.tryFind mainNodeName ctx.TriviaBefore
        |> Option.defaultValue []
        |> List.exists (fun ({ Range = r }: TriviaInstruction) -> RangeHelpers.rangeEq r range)

    sepConsideringTriviaContentBeforeBy findNode sepF range ctx

let sepNlnConsideringTriviaContentBeforeFor (mainNode: FsAstType) (range: Range) =
    sepConsideringTriviaContentBeforeForMainNode sepNln mainNode range

let sepNlnTypeAndMembers
    (withKeywordNodeType: FsAstType)
    (withKeywordRange: range option)
    (firstMemberRange: Range)
    (mainNodeType: FsAstType)
    (ctx: Context)
    : Context =
    let triviaBeforeWithKeyword: TriviaInstruction list =
        match withKeywordRange with
        | None -> []
        | Some withKeywordRange ->
            ctx.TriviaBefore
            |> Map.tryFindOrEmptyList withKeywordNodeType
            |> List.filter (fun tn -> RangeHelpers.rangeEq withKeywordRange tn.Range)

    match triviaBeforeWithKeyword with
    | [] ->
        if ctx.Config.NewlineBetweenTypeDefinitionAndMembers then
            sepNlnConsideringTriviaContentBeforeFor mainNodeType firstMemberRange ctx
        else
            ctx
    | triviaInstructions -> printTriviaInstructions triviaInstructions ctx

let sepNlnWhenWriteBeforeNewlineNotEmpty fallback (ctx: Context) =
    if hasWriteBeforeNewlineContent ctx then
        sepNln ctx
    else
        fallback ctx

let sepSpaceUnlessWriteBeforeNewlineNotEmpty (ctx: Context) =
    if hasWriteBeforeNewlineContent ctx then
        ctx
    else
        sepSpace ctx

let autoIndentAndNlnWhenWriteBeforeNewlineNotEmpty (f: Context -> Context) (ctx: Context) =
    if hasWriteBeforeNewlineContent ctx then
        indentSepNlnUnindent f ctx
    else
        f ctx

let autoNlnConsideringTriviaIfExpressionExceedsPageWidth sepNlnConsideringTriviaContentBefore expr (ctx: Context) =
    expressionExceedsPageWidth
        sepNone
        sepNone // before and after for short expressions
        sepNlnConsideringTriviaContentBefore
        sepNone // before and after for long expressions
        expr
        ctx

let addExtraNewlineIfLeadingWasMultiline leading sepNlnConsideringTriviaContentBefore continuation =
    leadingExpressionIsMultiline leading (fun ml ->
        sepNln +> onlyIf ml sepNlnConsideringTriviaContentBefore +> continuation)

let autoIndentAndNlnExpressUnlessRagnarok (f: SynExpr -> Context -> Context) (e: SynExpr) (ctx: Context) =
    match e with
    | SourceParser.StroustrupStyleExpr ctx.Config.ExperimentalStroustrupStyle e -> f e ctx
    | _ -> indentSepNlnUnindent (f e) ctx

let autoIndentAndNlnIfExpressionExceedsPageWidthUnlessRagnarok
    (f: SynExpr -> Context -> Context)
    (e: SynExpr)
    (ctx: Context)
    =
    match e with
    | SourceParser.StroustrupStyleExpr ctx.Config.ExperimentalStroustrupStyle e -> f e ctx
    | _ -> autoIndentAndNlnIfExpressionExceedsPageWidth (f e) ctx

type internal ColMultilineItem =
    | ColMultilineItem of
        // current expression
        expr: (Context -> Context) *
        // sepNln of current item
        sepNln: (Context -> Context)

type internal ColMultilineItemsState =
    { LastBlockMultiline: bool
      Context: Context }

/// Checks if the events of an expression produces multiple lines of by user code.
/// Leading or trailing trivia will not be counted as such.
let private isMultilineItem (expr: Context -> Context) (ctx: Context) : bool * Context =
    let previousEventsLength = ctx.WriterEvents.Length
    let nextCtx = expr ctx

    let isExpressionMultiline =
        Queue.skipExists
            previousEventsLength
            (function
            | WriteLine
            | WriteLineInsideStringConst -> true
            | _ -> false)
            (fun events ->
                if events.Length > 0 then
                    // filter leading newlines and trivia
                    match Array.head events with
                    | CommentOrDefineEvent _
                    | WriteLine
                    | WriteLineBecauseOfTrivia -> true
                    | _ -> false
                else
                    false)
            nextCtx.WriterEvents

    isExpressionMultiline, nextCtx

/// This helper function takes a list of expressions and ranges.
/// If the expression is multiline it will add a newline before and after the expression.
/// Unless it is the first expression in the list, that will never have a leading new line.
/// F.ex.
/// let a = AAAA
/// let b =
///     BBBB
///     BBBB
/// let c = CCCC
///
/// will be formatted as:
/// let a = AAAA
///
/// let b =
///     BBBB
///     BBBBB
///
/// let c = CCCC

let colWithNlnWhenItemIsMultiline (items: ColMultilineItem list) (ctx: Context) : Context =
    match items with
    | [] -> ctx
    | [ (ColMultilineItem (expr, _)) ] -> expr ctx
    | ColMultilineItem (initialExpr, _) :: items ->
        let result =
            // The first item can be written as is.
            let initialIsMultiline, initialCtx = isMultilineItem initialExpr ctx

            let itemsState =
                { Context = initialCtx
                  LastBlockMultiline = initialIsMultiline }

            let rec loop (acc: ColMultilineItemsState) (items: ColMultilineItem list) =
                match items with
                | [] -> acc.Context
                | ColMultilineItem (expr, sepNlnItem) :: rest ->
                    // Assume the current item will be multiline or the previous was.
                    // If this is the case, we have already processed the correct stream of event (with additional newline)
                    // It is cheaper to replay the current expression if it (and its predecessor) turned out to be single lines.
                    let ctxAfterNln =
                        (ifElseCtx
                            newlineBetweenLastWriteEvent
                            sepNone // don't add extra newline if there already is a full blank line at the end of the stream.
                            sepNln
                         +> sepNlnItem)
                            acc.Context

                    let isMultiline, nextCtx = isMultilineItem expr ctxAfterNln

                    let nextCtx =
                        if not isMultiline && not acc.LastBlockMultiline then
                            // both the previous and current items are single line expressions
                            // replay the current item as a fallback
                            (sepNlnItem +> expr) acc.Context
                        else
                            nextCtx

                    loop
                        { acc with
                            Context = nextCtx
                            LastBlockMultiline = isMultiline }
                        rest

            loop itemsState items

        result

let colWithNlnWhenItemIsMultilineUsingConfig (items: ColMultilineItem list) (ctx: Context) =
    if ctx.Config.BlankLinesAroundNestedMultilineExpressions then
        colWithNlnWhenItemIsMultiline items ctx
    else
        col sepNln items (fun (ColMultilineItem (expr, _)) -> expr) ctx
