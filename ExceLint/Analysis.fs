﻿namespace ExceLint
    open System.Collections.Generic

    module Analysis =

        // a C#-friendly configuration object that is also pure/fluent
        type FeatureConf private (userConf: Map<string,bool>) =
            let _base = Feature.BaseFeature.run 
            let _defaults = Map.ofSeq [
                ("indegree", false);
                ("combineddegree", false);
                ("outdegree", false);
            ]
            let _config = Map.fold (fun acc key value -> Map.add key value acc) _defaults userConf

            let _features = Map.ofSeq [
                ("indegree", fun (cell)(dag) -> if _config.["indegree"] then Degree.InDegree.run cell dag else _base cell dag);
                ("combineddegree", fun (cell)(dag) -> if _config.["combineddegree"] then (Degree.InDegree.run cell dag + Degree.OutDegree.run cell dag) else _base cell dag);
                ("outdegree", fun (cell)(dag) -> if _config.["outdegree"] then Degree.OutDegree.run cell dag else _base cell dag);
            ]

            new() = FeatureConf(Map.empty)

            // fluent constructors
            member self.enableInDegree() : FeatureConf =
                FeatureConf(_config.Add("indegree", true))
            member self.enableOutDegree() : FeatureConf =
                FeatureConf(_config.Add("outdegree", true))
            member self.enableCombinedDegree() : FeatureConf =
                FeatureConf(_config.Add("combineddegree", true))

            // getters
            member self.Feature
                with get(name) = _features.[name]
            member self.Features
                with get() = _features |> Map.toArray |> Array.map fst

        // a C#-friendly error model constructor
        type ErrorModel(config: FeatureConf, dag: Depends.DAG, alpha: double) =
            // train model on construction
            let _data =
                let allCells = dag.allCells()

                config.Features |>
                Array.map (fun fname ->
                    // get feature lambda
                    let feature = config.Feature(fname)

                    // run feature on every cell
                    let fvals = Array.map (fun cell -> feature cell dag) allCells
                    fname, fvals
                ) |>
                Map.ofArray

            /// <summary>Analyzes the given cell using all of the configured features and produces a score.</summary>
            /// <param name="cell">the address of a formula cell</param>
            /// <returns>a score</returns>
            member self.score(cell: AST.Address) : double =
                // get feature scores
                let fs = Array.map (fun fname ->
                            // get feature lambda
                            let f = config.Feature fname

                            // get feature value for this cell
                            let t = f cell dag

                            // determine probability
                            let p = BasicStats.cdf t _data.[fname]

                            // do two-tailed test
                            if p < (alpha / 2.0) || p > (1.0 - (alpha / 2.0)) then 1.0 else 0.0
                         ) (config.Features)

                // combine scores
                Array.sum fs

            /// <summary>Ranks all the cells in the workbook by their anomalousness.</summary>
            /// <returns>an AST.Address[] ranked from most to least anomalous</returns>
            member self.rank() : AST.Address[] =
                // get all cells
                dag.allCells() |>

                // rank by analysis score (rev to sort from high to low)
                Array.sortBy (self.score) |> Array.rev

            /// <summary>Ranks all the cells in the workbook by their anomalousness.</summary>
            /// <returns>an KeyValuePair<AST.Address,int>[] of (address,score) ranked from most to least anomalous</returns>
            member self.rankWithScore() : KeyValuePair<AST.Address,double>[] =
                // get all cells
                dag.allCells() |>

                // get scores
                Array.map (fun c -> new KeyValuePair<AST.Address,double>(c, self.score c)) |>

                // rank
                Array.sortBy (fun (pair: KeyValuePair<AST.Address, double>) -> -pair.Value)