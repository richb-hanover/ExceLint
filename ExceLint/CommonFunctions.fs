﻿namespace ExceLint
    open System
    open System.Collections.Generic
    open Utils
    open CommonTypes

    module CommonFunctions =
            // _analysis_base specifies which cells should be ranked:
            // 1. allCells means all cells in the spreadsheet
            // 2. onlyFormulas means only formulas
            let analysisBase(config: FeatureConf)(d: Depends.DAG) : AST.Address[] =
                let cs = if config.IsEnabled("AnalyzeOnlyFormulas") then
                            d.getAllFormulaAddrs()
                         else
                            d.allCells()
                let cs' = match config.IsLimitedToSheet with
                          | Some(wsname) -> cs |> Array.filter (fun addr -> addr.A1Worksheet() = wsname)
                          | None -> cs 
                cs'

            let runEnabledFeatures(cells: AST.Address[])(dag: Depends.DAG)(config: FeatureConf)(progress: Depends.Progress) =
                config.EnabledFeatures |>
                Array.map (fun fname ->
                    // get feature lambda
                    let feature = config.FeatureByName fname

                    let fvals =
                        Array.map (fun cell ->
                            if progress.IsCancelled() then
                                raise AnalysisCancelled

                            progress.IncrementCounter()
                            cell, feature cell dag
                        ) cells
                    
                    fname, fvals
                ) |> adict

            let invertedHistogram(scoretable: ScoreTable)(selcache: Scope.SelectorCache)(dag: Depends.DAG)(config: FeatureConf) : InvertedHistogram =
                assert (config.EnabledScopes.Length = 1 && config.EnabledFeatures.Length = 1)

                let d = new Dict<AST.Address,HistoBin>()

                Array.iter (fun fname ->
                    Array.iter (fun (sel: Scope.Selector) ->
                        Array.iter (fun (addr: AST.Address, score: Countable) ->
                            // fetch SelectID for this selector and address
                            let sID = sel.id addr dag selcache

                            // get binname
                            let binname = fname,sID,score

                            d.Add(addr,binname)
                        ) (scoretable.[fname])
                    ) (config.EnabledScopes)
                ) (config.EnabledFeatures)

                new InvertedHistogram(d)

            let centroid(c: seq<AST.Address>)(ih: InvertedHistogram) : Countable =
                c
                |> Seq.map (fun a ->
                    let (_,_,c) = ih.[a]    // get histobin for address
                    c                       // get countable from bin
                   )
                |> Countable.Mean               // get mean

            let cartesianProductByX(xset: Set<'a>)(yset: Set<'a>) : ('a*'a[]) list =
                // cartesian product, grouped by the first element,
                // excluding the element itself
                Set.map (fun x -> x, (Set.difference yset (Set.ofList [x])) |> Set.toArray) xset |> Set.toList

            let cartesianProduct(xs: seq<'a>)(ys: seq<'b>) : seq<'a*'b> =
                xs |> Seq.collect (fun x -> ys |> Seq.map (fun y -> x, y))

            let ToCountable(a: AST.Address)(ih: InvertedHistogram) : Countable =
                let (_,_,v) = ih.[a]
                v

            let nop = Depends.Progress.NOPProgress()

            let toDict(arr: ('a*'b)[]) : Dict<'a,'b> =
                // assumes that 'a is unique
                let d = new Dict<'a,'b>(arr.Length)
                Array.iter (fun (a,b) ->
                    d.Add(a,b)
                ) arr
                d

            let transitiveInputs(faddr: AST.Address)(dag : Depends.DAG) : AST.Address[] =
                let rec tf(addr: AST.Address) : AST.Address list =
                    if (dag.isFormula addr) then
                        // find all of the inputs (local dependencies) for formula
                        let refs_single = dag.getFormulaSingleCellInputs addr |> List.ofSeq
                        let refs_vector = dag.getFormulaInputVectors addr |>
                                                List.ofSeq |>
                                                List.map (fun rng -> rng.Addresses() |> Array.toList) |>
                                                List.concat
                        let refs = refs_single @ refs_vector
                        // prepend addr & recursively call this function
                        addr :: (List.map tf refs |> List.concat)
                    else
                        [addr]
    
                tf faddr |> List.toArray

            let refCount(dag: Depends.DAG) : Dict<AST.Address,int> =
                // for each input in the dependence graph, count how many formulas transitively refer to it
                let refcounts = Array.map (fun i -> i,(dag.getFormulasThatRefCell i).Length) (dag.allCells()) |> adict

                // if an input was not available at the time of dependence graph construction,
                // it will not be in dag.allCells() but formulas may refer to it; this
                // adds what refcount information we can discern from the visible parts
                // of the dependence graph
                for f in (dag.getAllFormulaAddrs()) do
                    let inputs = transitiveInputs f dag
                    for i in inputs do
                        if not (refcounts.ContainsKey i) then
                            refcounts.Add(i, 1)
                        else
                            refcounts.[i] <- refcounts.[i] + 1

                refcounts

            let intrinsicAnomalousnessWeights(analysis_base: Depends.DAG -> AST.Address[])(dag: Depends.DAG) : Weights =
                // get the set of cells to be analyzed
                let cells = analysis_base(dag)

                // determine how many formulas refer to each input
                let refcounts = refCount dag

                // for each cell, compute cumulative reference count. the insight here
                // is that summary rows are counting things that are counted by
                // subcomputations; thus, we should inflate their ranks by how much
                // they over-count.
                // this really only makes sense for formulas, but in case the user
                // asked for a ranking of all cells, we compute refcounts here even
                // for non-formulas
                let weights = Array.map (fun f ->
                                  let inputs = transitiveInputs f dag
                                  let weight = double (Array.sumBy (fun i -> refcounts.[i]) inputs)
                                  f,weight
                              ) cells

                weights |> dict

            let noWeights(analysis_base: Depends.DAG -> AST.Address[])(dag: Depends.DAG) : Weights =
                // get the set of cells to be analyzed
                let cells = analysis_base(dag)

                // for each cell, compute cumulative reference count. the insight here
                // is that summary rows are counting things that are counted by
                // subcomputations; thus, we should inflate their ranks by how much
                // they over-count.
                // this really only makes sense for formulas, but in case the user
                // asked for a ranking of all cells, we compute refcounts here even
                // for non-formulas
                let weights = Array.map (fun f ->
                                  f,1.0
                              ) cells

                weights |> dict

            let weights(input: Input)(analysis: Analysis) : AnalysisOutcome =
                // compute weights
                let weights = if input.config.IsEnabled "WeightByIntrinsicAnomalousness" then
                                  intrinsicAnomalousnessWeights (analysisBase input.config) input.dag
                              else
                                  noWeights (analysisBase input.config) input.dag

                match analysis with
                | Histogram h -> Success(Histogram({ h with weights = weights }))
                | COF c -> Success(COF({ c with weights = weights }))
                | Cluster c -> Success(Cluster({ c with weights = weights }))

            // sanity check: are scores monotonically increasing?
            let monotonicallyIncreasing(r: Ranking) : bool =
                let mutable last = 0.0
                let mutable outcome = true
                for kvp in r do
                    if kvp.Value >= last then
                        last <- kvp.Value
                    else
                        outcome <- false
                outcome

            // "AngleMin" algorithm
            let dderiv(y: double[]) : int =
                let mutable anglemin = 1.0
                let mutable angleminindex = 0
                for index in 0..(y.Length - 3) do
                    let angle = y.[index] + y.[index + 2] - 2.0 * y.[index + 1]
                    if angle < anglemin then
                        anglemin <- angle
                        angleminindex <- index
                angleminindex

            let equivalenceClasses(ranking: Ranking) : Dict<AST.Address,int> =
                let rankgrps = Array.groupBy (fun (kvp: KeyValuePair<AST.Address,double>) -> kvp.Value) ranking

                let grpids = Array.mapi (fun i (hb,_) -> hb,i) rankgrps |> adict

                let output = Array.map (fun (kvp: KeyValuePair<AST.Address,double>) -> kvp.Key, grpids.[kvp.Value]) ranking |> adict

                output

            let seekEquivalenceBoundary(ranking: Ranking)(cut_idx: int) : int =
                // if we have no anomalies, because either the cut index excludes
                // all cells or because the ranking is zero-length, just return -1
                if cut_idx = -1 || ranking.Length = 0 then
                    -1
                else
                    // a map from AST.Address to equivalence class number
                    let ecs = equivalenceClasses ranking

                    // get the equivalence class of the element at the cut index (inclusive)
                    let cutEC = ecs.[ranking.[cut_idx].Key]

                    // the last index in the ranking is
                    let lastidx = ranking.Length - 1

                    // if there is a "next element" in the ranking after the cut,
                    // is it in the same equivalence class?
                    if lastidx >= cut_idx + 1 && cutEC = ecs.[ranking.[cut_idx + 1].Key] then
                        // find the first index that is different by scanning backward
                        if cut_idx <= 0 then
                            -1
                        else
                            // find the index in the ranking where the equivalence class changes
                            let mutable seek_idx = cut_idx - 1
                            while seek_idx >= 0 && ecs.[ranking.[seek_idx].Key] = cutEC do
                                seek_idx <- seek_idx - 1
                            seek_idx
                    else
                        cut_idx

            // returns the index of the last element to KEEP
            // returns -1 if you should keep nothing
            let private findCutIndex(ranking: Ranking)(thresh_idx: int): int =
                if ranking.Length = 0 then
                    -1
                else
                    // extract totally-ordered score vector
                    let rank_nums = Array.map (fun (kvp: KeyValuePair<AST.Address,double>) -> kvp.Value) ranking

                    // cut it at thresh (inclusive)
                    let rank_nums' = rank_nums.[..thresh_idx]

                    // find the index of the "knee"
                    dderiv(rank_nums')

            let kneeIndexOpt(input: Input)(analysis: Analysis) : AnalysisOutcome =
                let ranking = match analysis with 
                              | Histogram h -> h.ranking 
                              | COF c -> c.ranking 
                              | Cluster c -> c.ranking

                let sig_threshold_idx =
                    match analysis with 
                    | Histogram h -> h.sig_threshold_idx 
                    | COF c -> c.sig_threshold_idx 
                    | Cluster c -> c.sig_threshold_idx

                let idx =
                    if input.config.IsEnabledSpectralRanking then
                        // compute knee cutoff
                        findCutIndex ranking sig_threshold_idx
                    else
                        // stick with total %
                        sig_threshold_idx
                // does the cut index straddle an equivalence class?
                let ce = seekEquivalenceBoundary ranking idx

                match analysis with
                | Histogram h -> Success(Histogram({ h with cutoff_idx = ce }))
                | COF c -> Success(COF({ c with cutoff_idx = ce }))
                | Cluster c -> Success(Cluster({ c with cutoff_idx = ce }))

            let cutoffIndex(input: Input)(analysis: Analysis) : AnalysisOutcome =
                let ranking = match analysis with | Histogram h -> h.ranking | COF c -> c.ranking | Cluster c -> c.ranking

                // compute total mass of distribution
                let total_mass = double ranking.Length
                // compute the index of the maximum element
                let idx = int (Math.Floor(total_mass * input.alpha))

                match analysis with
                | Histogram h -> Success(Histogram({ h with sig_threshold_idx = idx }))
                | COF c -> Success(COF({ c with sig_threshold_idx = idx }))
                | Cluster c -> Success(Cluster({ c with sig_threshold_idx = idx }))

            let canonicalSort(input: Input)(analysis: Analysis) : AnalysisOutcome =
                let r = match analysis with | Histogram h -> h.ranking | COF c -> c.ranking | Cluster c -> c.ranking
                let arr = Array.sortWith (fun (a: KeyValuePair<AST.Address,double>)(b: KeyValuePair<AST.Address,double>) ->
                                              let a_addr: AST.Address = a.Key
                                              let a_score: double = a.Value
                                              let b_addr: AST.Address = b.Key
                                              let b_score: double = b.Value

                                              if a_score < b_score then
                                                  -1
                                              elif a_score = b_score then
                                                  if a_addr.Col < b_addr.Col then
                                                      -1
                                                  elif a_addr.Col = b_addr.Col then
                                                      if a_addr.Row < b_addr.Row then
                                                          -1
                                                      elif a_addr.Row = b_addr.Row then
                                                          0
                                                      else
                                                          1
                                                  else
                                                      1
                                              else
                                                  1
                                         ) r
                assert (monotonicallyIncreasing arr)

                match analysis with
                | Histogram h -> Success(Histogram({ h with ranking = arr }))
                | COF c -> Success(COF({ c with ranking = Array.rev arr }))
                | Cluster c -> Success(Cluster({ c with ranking = Array.rev arr }))

            let reweightRanking(input: Input)(analysis: Analysis) : AnalysisOutcome =
                let ranking = match analysis with | Histogram h -> h.ranking | COF c -> c.ranking | Cluster c -> c.ranking
                let weights = match analysis with | Histogram h -> h.weights | COF c -> c.weights | Cluster c -> c.weights

                let ranking' = 
                    ranking
                    |> Array.map (fun (kvp: KeyValuePair<AST.Address,double>) ->
                        let addr = kvp.Key
                        let score = kvp.Value
                        new KeyValuePair<AST.Address,double>(addr, weights.[addr] * score)
                      )

                match analysis with
                | Histogram h -> Success(Histogram({ h with ranking = ranking' }))
                | COF c -> Success(COF({ c with ranking = ranking' }))
                | Cluster c -> Success(Cluster({ c with ranking = ranking' }))