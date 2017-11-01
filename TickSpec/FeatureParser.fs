﻿module TickSpec.FeatureParser

open System.Text.RegularExpressions
open TickSpec.LineParser
open TickSpec.BlockParser

/// Computes combinations of table values
let internal computeCombinations (tables:Table []) =
    let rec combinations source =
        match source with
        | [] -> [[]]
        | (header, rows) :: xs ->
            [ for row in rows do
                for combinedRow in combinations xs ->
                    (header, row) :: combinedRow ]
    
    let processRow rowSet =
        rowSet
        |> List.fold (fun state (_header, rowData) ->
            match state with
            | None -> None
            | Some (stateTags, stateMap) ->
                rowData |> 
                ( fun (rowTags, rowMap) ->                
                    let newStateMap =
                        rowMap
                        |> Map.fold (fun state key value -> 
                            match state with
                            | None -> None
                            | Some map -> 
                                let existingValue = map |> Map.tryFind key
                                match existingValue with
                                | None -> Some (map.Add (key, value))
                                | Some x when x = value -> Some map
                                | _ -> None 
                        ) (Some stateMap)
                    match newStateMap with
                    | Some stateMap -> 
                        let newStateTags = List.append stateTags rowTags
                        Some (newStateTags, stateMap)
                    | None -> None                
                )
        ) (Some (List.Empty, Map.empty))

    tables
    |> Seq.map 
        ((fun table -> table.Header, table.Tags, table.Rows) >>
        (fun (header, tags, rows) -> 
            header |> Array.toList |> List.sort,
            rows
            |> Seq.map (fun row ->
                tags |> Array.toList |> List.sort,
                row |> Array.mapi (fun i col -> header.[i], col) |> Map.ofArray)
        ))
    // Union tables with the same columns
    |> Seq.groupBy (fun (header, _) -> header)
    |> Seq.map (fun (header, tables) ->
        header,
        Seq.collect (fun (_, rows) -> rows) tables)
    |> Seq.toList
    // Cross-join tables with different columns
    |> combinations
    |> List.map processRow
    |> List.choose id
    |> List.groupBy (fun (_tags, row) -> row)
    |> List.map (fun (row, taggedRow) -> 
        taggedRow 
        |> List.fold( fun tags (rowTags, _row) ->
            List.append tags rowTags
        ) List.empty,
        row
    )
    |> List.map ( fun (tags,rows) ->
        tags,
        rows |> Map.toList
    )

/// Replace line with specified named values
let internal replaceLine (xs:seq<string * string>) (scenario,n,tags,line,step) =
    let replace s =
        let lookup (m:Match) =
            let x = m.Value.TrimStart([|'<'|]).TrimEnd([|'>'|])
            xs |> Seq.tryFind (fun (k,_) -> k = x)
            |> (function Some(_,v) -> v | None -> m.Value)
        let pattern = "<([^<]*)>"
        Regex.Replace(s, pattern, lookup)
    let step = 
        match step with
        | GivenStep s -> replace s |> GivenStep
        | WhenStep s -> replace s |> WhenStep
        | ThenStep s  -> replace s |> ThenStep
    let table =
        line.Table 
        |> Option.map (fun table ->
            Table(table.Header,
                table.Rows |> Array.map (fun row ->
                    row |> Array.map (fun col -> replace col)
                )
            )
        )
    let bullets =
        line.Bullets
        |> Option.map (fun bullets -> bullets |> Array.map replace)
    (scenario,n,tags,{line with Table=table;Bullets=bullets},step)

/// Appends shared examples to scenarios as examples
let internal appendSharedExamples (sharedExamples:Table[]) scenarios  =
    if Seq.length sharedExamples = 0 then
        scenarios
    else
        scenarios |> Seq.map (function 
            | scenarioName,tags,steps,None ->
                scenarioName,tags,steps,Some(sharedExamples)
            | scenarioName,tags,steps,Some(exampleTables) ->
                scenarioName,tags,steps,Some(Array.append exampleTables sharedExamples)
        )

/// Parses lines of feature
let parseFeature (lines:string[]) =
    let toStep (_,_,_,line,step) = step,line
    let featureName,background,scenarios,sharedExamples = parseBlocks lines     
    let scenarios =
        scenarios 
        |> appendSharedExamples sharedExamples
        |> Seq.collect (function
            | name,tags,steps,None ->
                let steps = 
                    Seq.append background steps
                    |> Seq.map toStep 
                    |> Seq.toArray
                Seq.singleton
                    { Name=name; Tags=tags; Steps=steps; Parameters=[||] }
            | name,tags,steps,Some(exampleTables) ->
                /// All combinations of tables
                let taggedCombinations = computeCombinations exampleTables
                // Execute each combination
                taggedCombinations |> Seq.mapi (fun i (tableTags, combination) ->
                    let name = sprintf "%s(%d)" name i
                    let tags = Array.append tags (tableTags |> List.toArray) 
                    let combination = combination |> Seq.toArray
                    let steps =
                        Seq.append background steps
                        |> Seq.map (replaceLine combination)
                        |> Seq.map toStep
                        |> Seq.toArray
                    { Name=name; Tags=tags; Steps=steps; Parameters=combination }
                )
        )
    { Name=featureName; Scenarios=scenarios |> Seq.toArray }