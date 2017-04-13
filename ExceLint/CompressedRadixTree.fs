﻿namespace ExceLint

    open System.Collections.Generic
    open UInt128

    module CRTUtil =
        let nBitMask(n: int) : UInt128 =
            UInt128.Sub (UInt128.LeftShift UInt128.One n) UInt128.One

        let calcMask(startpos: int)(endpos: int) : UInt128 =
            UInt128.LeftShift (nBitMask(endpos - startpos + 1)) (127 - startpos)

    [<AbstractClass>]
    // endpos is inclusive
    type CRTNode<'a>(endpos: int, prefix: UInt128) =
        abstract member IsLeaf: bool
        abstract member IsEmpty: bool
        abstract member Lookup: UInt128 -> 'a option
        abstract member Replace: UInt128 -> 'a -> CRTNode<'a>
        
    and CRTRoot<'a>(left: CRTNode<'a>, right: CRTNode<'a>) =
        inherit CRTNode<'a>(-1, UInt128.Zero)
        let topbit = UInt128.LeftShift UInt128.One 127
        override self.IsLeaf = false
        override self.IsEmpty = false
        override self.Lookup(key: UInt128) : 'a option =
            // is the higest-order bit 0 or 1?
            if UInt128.GreaterThan topbit key then
                // top bit is 0
                left.Lookup key
            else
                // top bit is 1
                right.Lookup key
        override self.Replace(key: UInt128)(value: 'a) : CRTNode<'a> =
            if UInt128.GreaterThan topbit key then
                // top bit is 0, replace left
                CRTRoot(left.Replace key value, right) :> CRTNode<'a>
            else
                // top bit is 1, replace right
                CRTRoot(left, right.Replace key value) :> CRTNode<'a>

    and CRTInner<'a>(endpos: int, prefix: UInt128, left: CRTNode<'a>, right: CRTNode<'a>) =
        inherit CRTNode<'a>(endpos, prefix)
        let mask = CRTUtil.calcMask 0 endpos
        let mybits = UInt128.BitwiseAnd mask prefix
        let nextBitMask = CRTUtil.calcMask (endpos + 1) (endpos + 1)
        override self.IsLeaf = false
        override self.IsEmpty = false
        override self.Lookup(key: UInt128) : 'a option =
            let keybits = UInt128.BitwiseAnd mask key
            if UInt128.Equals mybits keybits then
                let nextbit = UInt128.BitwiseAnd nextBitMask key
                if UInt128.GreaterThan nextBitMask nextbit then
                    left.Lookup key
                else
                    right.Lookup key
            else
                None
        override self.Replace(key: UInt128)(value: 'a) : CRTNode<'a> =
            let keybits = UInt128.BitwiseAnd mask key
            if UInt128.Equals mybits keybits then
                let nextbit = UInt128.BitwiseAnd nextBitMask key
                if UInt128.GreaterThan nextBitMask nextbit then
                    CRTInner(endpos, prefix, left.Replace key value, right) :> CRTNode<'a>
                else
                    CRTInner(endpos, prefix, left, right.Replace key value) :> CRTNode<'a>
            else
                // insert a new parent
                // find longest common prefix
                let pidx = UInt128.LongestCommonPrefix key prefix
                let mask' = CRTUtil.calcMask 0 endpos
                let prefix' = UInt128.BitwiseAnd mask' key

                // insert current subtree on the left or on the right of new parent node?
                let nextBitMask' = CRTUtil.calcMask (pidx + 1) (pidx + 1)
                let nextbit = UInt128.BitwiseAnd nextBitMask' prefix
                if UInt128.GreaterThan nextBitMask' nextbit then
                    // current node goes on the left
                    CRTInner(pidx, prefix', self, CRTLeaf(key, value)) :> CRTNode<'a>
                else
                    // current node goes on the right
                    CRTInner(pidx, prefix', CRTLeaf(key, value), self) :> CRTNode<'a>

    and CRTLeaf<'a>(prefix: UInt128, value: 'a) =
        inherit CRTNode<'a>(127, prefix)
        override self.IsLeaf = true
        override self.IsEmpty = false
        override self.Lookup(str: UInt128) : 'a option = Some value
        override self.Replace(key: UInt128)(value: 'a) : CRTNode<'a> =
            CRTLeaf(prefix, value) :> CRTNode<'a>

    and CRTEmptyLeaf<'a>(prefix: UInt128) =
        inherit CRTNode<'a>(127, prefix)
        override self.IsLeaf = true
        override self.IsEmpty = true
        override self.Lookup(str: UInt128) : 'a option = None
        override self.Replace(key: UInt128)(value: 'a) : CRTNode<'a> =
            CRTLeaf(prefix, value) :> CRTNode<'a>
