﻿namespace ExceLint
    open System.Collections.Generic
    open System.Collections
    open System
    open ConfUtils

        type ScoreTable = Dict<string,(AST.Address*double)[]>
        type FreqTable = Dict<string*Scope.SelectID*double,int>
        type Ranking = KeyValuePair<AST.Address,double>[]

        // a C#-friendly error model constructor
        type ErrorModel(config: FeatureConf, dag: Depends.DAG, alpha: double, progress: Depends.Progress) =
                
            let runEnabledFeatures() =
                config.EnabledFeatures |>
                Array.map (fun fname ->
                    // get feature lambda
                    let feature = config.FeatureByName fname

                    let fvals =
                        Array.map (fun cell ->
                            progress.IncrementCounter()
                            cell, feature cell dag
                        ) (dag.allCells())
                    
                    fname, fvals
                ) |> adict
            
            let buildFrequencyTable(data: ScoreTable): FreqTable =
                let d = new Dict<string*Scope.SelectID*double,int>()
                Array.iter (fun fname ->
                    Array.iter (fun (sel: Scope.Selector) ->
                        Array.iter (fun (addr: AST.Address, score: double) ->
                            let sID = sel.id addr
                            if d.ContainsKey (fname,sID,score) then
                                let freq = d.[(fname,sID,score)]
                                d.[(fname,sID,score)] <- freq + 1
                            else
                                d.Add((fname,sID,score), 1)
                            progress.IncrementCounter()
                        ) (data.[fname])
                    ) (Scope.Selector.Kinds)
                ) (config.EnabledFeatures)
                d

            /// <summary>Analyzes the given cell using all of the configured features and produces a score.</summary>
            /// <param name="cell">the address of a formula cell</param>
            /// <returns>a score</returns>
            let score(cell: AST.Address)(fname: string) : double =
                // get feature by name
                let f = config.FeatureByName fname

                // get feature value for this cell
                f cell dag

            let rankByFeatureAndSelector(fname: string)(dag: Depends.DAG)(sel: Scope.Selector)(ftable: FreqTable) : (AST.Address*int)[] =
                // sort by least common
                let sorted = Array.map (fun addr ->
                                 let sID = sel.id addr
                                 let score = score addr fname
                                 let freq = ftable.[(fname,sID,score)]
                                 addr, freq
                             ) (dag.allCells()) |>
                             Array.sortBy (fun (addr,freq) -> freq)
                // return the address and its rank
                sorted |> Array.mapi (fun i (addr,freq) -> addr,i )

            let rankAllFeatures(dag: Depends.DAG)(sel: Scope.Selector)(ftable: FreqTable) : Dict<AST.Address,int[]> =
                // get per-feature rank for every cell
                let d = new Dict<AST.Address,int[]>()

                Array.iteri (fun i fname ->
                    let ranks = rankByFeatureAndSelector fname dag sel ftable
                    Array.iter (fun (addr,rank) ->
                        if d.ContainsKey addr then
                            let arr = d.[addr]
                            arr.[i] <- rank
                            d.[addr] <- arr
                        else
                            let arr = Array.zeroCreate(config.EnabledFeatures.Length)
                            arr.[i] <- rank
                            d.Add(addr, arr)
                    ) ranks
                ) (config.EnabledFeatures)
                d

            let sumFeatureRanksAndSort(ranks: Dict<AST.Address,int[]>) : Ranking =
                Seq.map (fun (kvp: KeyValuePair<AST.Address,int[]>) ->
                    new KeyValuePair<AST.Address,double>(kvp.Key, double(Array.sum (kvp.Value)))
                ) ranks
                |> Seq.toArray
                |> Array.sortBy (fun kvp -> kvp.Value)

            // sum the count of the appropriate feature bin of every feature
            // for the given address
            let sumFeatureCounts(addr: AST.Address)(sel: Scope.Selector)(ftable: FreqTable) : int =
                Array.sumBy (fun fname -> 
                    // get feature
                    let feature = config.FeatureByName fname
                    // get selector ID
                    let sID = sel.id addr
                    // get feature score
                    let fscore = feature addr dag
                    // get score count
                    ftable.[(fname,sID,fscore)]
                ) (config.EnabledFeatures)

            // "AngleMin" algorithm
            let dderiv(y: int[]) : int =
                let mutable anglemin = 1
                let mutable angleminindex = 0
                for index in 0..(y.Length - 3) do
                    let angle = y.[index] + y.[index + 2] - 2 * y.[index + 1]
                    if angle < anglemin then
                        anglemin <- angle
                        angleminindex <- index
                angleminindex

            /// <summary>
            /// Finds the maxmimum significance height
            /// </summary>
            /// <returns>maxmimum significance height count (an int)</returns>
            let _significanceThreshold : int =
                // round to integer
                int (
                    // get total number of counts
                    double (dag.allCells().Length * config.EnabledFeatures.Length * config.EnabledScopes.Length)
                    // times signficance
                    * alpha
                )

            let findCutIndex(ranking: Ranking): int =
                let sigThresh = _significanceThreshold

                // compute total order
                let rank_nums = Array.map (fun (kvp: KeyValuePair<AST.Address,double>) -> int(kvp.Value)) ranking

                // find the index of the "knee"
                let dderiv_idx = dderiv(rank_nums)

                // cut the ranking at the knee index
                let knee_cut = ranking.[0..dderiv_idx]

                // the ranking may include scores above the significance threshold, so
                // scan through the list to find the index of the last significant score
                let cut_idx: int = knee_cut
                                    |> Array.mapi (fun i elem -> (i,elem))
                                    |> Array.fold (fun (acc: int)(i: int, score: KeyValuePair<AST.Address,double>) ->
                                        if score.Value > double sigThresh then
                                            acc
                                        else
                                            i
                                        ) (knee_cut.Length - 1)

                cut_idx

            let rank(ftable: FreqTable) : Ranking =
                // get sums for every address
                // and for every enabled scope
                let addrSums: (AST.Address*int)[] =
                    Array.map (fun addr ->
                        let sum = Array.sumBy (fun sel ->
                                        sumFeatureCounts addr Scope.Selector.AllCells ftable
                                  ) (config.EnabledScopes)
                        addr, sum
                    ) (dag.allCells())

                // rank by sum (smallest first)
                let rankedAddrs: (AST.Address*int)[] = Array.sortBy (fun (addr,sum) -> sum) addrSums

                // return KeyValuePairs
                Array.map (fun (addr,sum) -> new KeyValuePair<AST.Address,double>(addr,double sum)) rankedAddrs

            let tryAbsolute(addr: AST.Address, faddr: AST.Address) : unit =
                let ast = dag.getASTofFormulaAt(faddr)

                // find all references to addr


                failwith "yay"

            let inferAddressModes(r: Ranking) : Ranking =
                // convert ranking into map
                let scores = r |> Array.map (fun (pair: KeyValuePair<AST.Address,double>) -> (pair.Key,pair.Value))
                               |> Map.ofArray

                // for each input cell, try changing all refs to either abs or rel;
                // if anomalousness drops, ascribe new semantics to cell
                dag.allCells()
                    |> Array.map (fun cell ->
                           let cf = dag.getFormulasThatRefCell cell

                           // get the anomalousness of each cell's referencing formulas
                           let scores = cf |> Array.map (fun f -> scores.[f])

                           // get ASTs
                           let asts = cf |> Array.map (fun f -> dag.getASTofFormulaAt f)

                           // for each referencing f, systematically generate all ref variants
                           let fs' = cf |> Array.mapi (fun i f ->
                                               let ast = asts.[i]

                                               let mutator = ASTMutator.mutateExpr ast cell

                                               let cabs_rabs = mutator AST.AddressMode.Absolute AST.AddressMode.Absolute
                                               let cabs_rrel = mutator AST.AddressMode.Absolute AST.AddressMode.Relative
                                               let crel_rabs = mutator AST.AddressMode.Relative AST.AddressMode.Absolute
                                               let crel_rrel = mutator AST.AddressMode.Relative AST.AddressMode.Relative

                                               (cabs_rabs, cabs_rrel, crel_rabs, crel_rrel)
                                           )

                            // for each f, create a new model


                           

                           failwith "no"
                       )

            let runModel() =
                // get scores for each feature: featurename -> (address, score)[]
                let (scores: ScoreTable,score_time: int64) = PerfUtils.runMillis runEnabledFeatures ()

                // build frequency table: (featurename, selector, score) -> freq
                let ftable,ftable_time = PerfUtils.runMillis buildFrequencyTable scores

                // rank
                let ranking,ranking_time = PerfUtils.runMillis rank ftable

                (scores, ftable, ranking, score_time, ftable_time, ranking_time)

            let (_scores, _ftable, _ranking, _score_time, _ftable_time, _ranking_time) = runModel()

            // rank cutoff
            let _cutoff = findCutIndex _ranking

            member self.ScoreTimeInMilliseconds : int64 = _score_time

            member self.FrequencyTableTimeInMilliseconds : int64 = _ftable_time

            member self.RankingTimeInMilliseconds : int64 = _ranking_time

            member self.NumScoreEntries : int = Array.fold (fun acc (pairs: (AST.Address*double)[]) -> acc + pairs.Length) 0 (_scores.Values |> Seq.toArray)

            member self.NumFreqEntries : int = _ftable.Count

            member self.NumRankedEntries : int = dag.allCells().Length

            member self.rankByFeatureSum() : Ranking = _ranking

            member self.getSignificanceCutoff : int = _cutoff

            member self.inspectSelectorFor(addr: AST.Address, sel: Scope.Selector) : KeyValuePair<AST.Address,(string*double)[]>[] =
                let sID = sel.id addr

                let d = new Dict<AST.Address,(string*double) list>()

                Seq.iter (fun (kvp: KeyValuePair<string,(AST.Address*double)[]>) ->
                    let fname: string = kvp.Key
                    let scores: (AST.Address*double)[] = kvp.Value

                    let valid_scores =
                        Array.choose (fun (addr2,score) ->
                            if sel.id addr2 = sID then
                                Some (addr2,score)
                            else
                                None
                        ) scores

                    Array.iter (fun (addr2,score) ->
                        if d.ContainsKey addr2 then
                            d.[addr2] <- (fname,score) :: d.[addr2]
                        else
                            d.Add(addr2, [(fname,score)])
                    ) valid_scores
                ) _scores

                let debug = Seq.map (fun (kvp: KeyValuePair<AST.Address,(string*double) list>) ->
                                        let addr2: AST.Address = kvp.Key
                                        let scores: (string*double)[] = kvp.Value |> List.toArray

                                        new KeyValuePair<AST.Address,(string*double)[]>(addr2, scores)
                                     ) d

                debug |> Seq.toArray

