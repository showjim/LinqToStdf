﻿// (c) Copyright Mark Miller.
// This source is subject to the Microsoft Public License.
// See http://www.microsoft.com/resources/sharedsource/licensingbasics/sharedsourcelicenses.mspx.
// All other rights reserved.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Linq.Expressions;
using LinqToStdf.Records.V4;
using System.Reflection;
using System.Collections.ObjectModel;
using System.Collections;
using System.Diagnostics;

namespace LinqToStdf.Indexing {
    /// <summary>
    /// This indexing strategy aims to provide performant, memory-efficient indexing of V4 STDFs that improve query performance
    /// </summary>
    public class V4StructureIndexingStrategy : CachingIndexingStrategy {

        /// <summary>
        /// Describes the scope of a section of the stdf file, like the records in the scope of a particular part, wafer, etc.
        /// </summary>
        class Extents {
            /// <summary>
            /// The starting record index
            /// </summary>
            public int StartIndex { get; set; }
            /// <summary>
            /// The starting file offset
            /// </summary>
            public long StartOffset { get; set; }
            /// <summary>
            /// The ending record index
            /// </summary>
            public int EndIndex { get; set; }
            /// <summary>
            /// The ending file offset
            /// </summary>
            public long EndOffset { get; set; }
        }

        /// <summary>
        /// Maps records to "extents" of different scopes.  Also capable of mapping outer extents to extents
        /// </summary>
        class ParentMap {

            /// <summary>
            /// A list of extents from the file
            /// </summary>
            readonly List<Extents> _ExtentsList = new List<Extents>();
            /// <summary>
            /// Gets the extents containing the specified record
            /// </summary>
            public Extents? GetExtents(StdfRecord record) {
                //TODO: optimized search (binary?)
                var candidate = _ExtentsList.TakeWhile(e => e.StartOffset <= record.Offset)
                    .LastOrDefault();
                if (candidate is null) return null;
                return (candidate.EndOffset >= record.Offset) ? candidate : null;
            }

            /// <summary>
            /// Enumerates the extents that are fully within the specified outer extents
            /// </summary>
            public IEnumerable<Extents> GetExtentsListWithin(Extents outerExtents) {
                return _ExtentsList.SkipWhile(e => e.StartIndex < outerExtents.StartIndex)
                    .TakeWhile(e => e.EndIndex < outerExtents.EndIndex);
            }

            /// <summary>
            /// Gets all the extents in the map
            /// </summary>
            public IEnumerable<Extents> GetAllExtents() {
                return from e in _ExtentsList select e;
            }

            /// <summary>
            /// Adds extents to the map
            /// </summary>
            public void AddExtents(Extents extents) {
                _ExtentsList.Add(extents);
            }
        }

        //we keep track of the structural scopes
        Mir? _Mir;
        Mrr? _Mrr;
        readonly List<Pcr> _Pcrs = new List<Pcr>();
        List<StdfRecord>? _AllRecords = null;
        readonly ParentMap _PartsMap = new ParentMap();
        readonly ParentMap _WafersMap = new ParentMap();

        /// <summary>
        /// Finds all the records in the specified extents
        /// </summary>
        IEnumerable<StdfRecord> GetRecordsInExtents(Extents extents) {
            Debug.Assert(_AllRecords is not null);
            for (var i = extents.StartIndex; i <= extents.EndIndex; i++) {
                yield return _AllRecords[i];
            }
        }

        /// <summary>
        /// Finds all the records in the specified extents in reverse order
        /// </summary>
        /// <param name="extents"></param>
        /// <returns></returns>
        IEnumerable<StdfRecord> GetRecordsInExtentsReverse(Extents extents) {
            Debug.Assert(_AllRecords is not null);
            for (var i = extents.EndIndex; i >= extents.StartIndex; i--) {
                yield return _AllRecords[i];
            }
        }

        /// <summary>
        /// This is the method that is used to index a stream of records
        /// </summary>
        public override void IndexRecords(IEnumerable<StdfRecord> records) {
            _AllRecords = new List<StdfRecord>();
            //extents for the current wafer and part
            Extents? currentWaferExtents = null;
            Extents? currentPartExtents = null;
            //tells us we're looking for something to confirm the extents are complete (helps us deal with multi-site testing)
            bool waferEnding = false;
            bool partsEnding = false;
            //ends the current wafer extents at the specified index
            void EndWafer(int endIndex)
            {
                Debug.Assert(currentWaferExtents is not null);
                currentWaferExtents.EndIndex = endIndex;
                currentWaferExtents.EndOffset = _AllRecords[endIndex].Offset;
                _WafersMap.AddExtents(currentWaferExtents);
                currentWaferExtents = null;
                waferEnding = false;
            }
            //ends the current part extents at the specified index
            void EndParts(int endIndex)
            {
                Debug.Assert(currentPartExtents is not null);
                currentPartExtents.EndIndex = endIndex;
                currentPartExtents.EndOffset = _AllRecords[endIndex].Offset;
                _PartsMap.AddExtents(currentPartExtents);
                currentPartExtents = null;
                partsEnding = false;
            }
            //loop through the records, building the structure
            foreach (var r in records) {
                var index = _AllRecords.Count;
                //look for marker records
                //TODO: does all this checking/casting slow us down too much?
                if (r.GetType() == typeof(Mir)) _Mir = (Mir)r;
                else if (r.GetType() == typeof(Mrr)) _Mrr = (Mrr)r;
                else if (r.GetType() == typeof(Pcr)) _Pcrs.Add((Pcr)r);
                //if we think we're looking for the end of the wafers, and we hit something other than Wrr, we passed the end
                if (waferEnding && r.GetType() != typeof(Wrr)) {
                    EndWafer(index - 1);
                }
                //if we think we're looking for the end of the parts, and we hit something other than Prr, we passed the end
                if (partsEnding && r.GetType() != typeof(Prr))
                {
                    EndParts(index - 1);
                }
                //when we hit Wrr or Prr, start looking for something else
                if (r.GetType() == typeof(Wrr)) {
                    waferEnding = true;
                }
                else if (r.GetType() == typeof(Prr)) {
                    partsEnding = true;
                }
                //if it's a new Pir/Wir, start new extents
                if (r.GetType() == typeof(Pir) && currentPartExtents == null) {
                    currentPartExtents = new Extents {
                        StartIndex = index,
                        StartOffset = r.Offset
                    };
                }
                if (r.GetType() == typeof(Wir) && currentWaferExtents == null) {
                    currentWaferExtents = new Extents {
                        StartIndex = index,
                        StartOffset = r.Offset
                    };
                }
                _AllRecords.Add(r);
            }
            var lastIndex = _AllRecords.Count - 1;
            //end any open wafers/parts at the last index
            if (waferEnding) {
                EndWafer(lastIndex);
            }
            if (partsEnding) {
                EndParts(lastIndex);
            }
        }

        /// <summary>
        /// Enumerates the records that we've indexed
        /// </summary>
        /// <returns></returns>
        public override IEnumerable<StdfRecord> EnumerateIndexedRecords() {
            return from r in _AllRecords select r;
        }

        /// <summary>
        /// Transform a query to use the indexing strategy
        /// </summary>
        public override System.Linq.Expressions.Expression TransformQuery(Expression query) {
            return new OptimizingVisitor(this).Visit(query);
        }

        /// <summary>
        /// Expression visitor that rewrites queries to leverage the indexed data
        /// </summary>
        class OptimizingVisitor : ExpressionVisitor {
            readonly V4StructureIndexingStrategy _Strategy;
            public OptimizingVisitor(V4StructureIndexingStrategy strategy) {
                _Strategy = strategy;
            }

            #region Optimizing Map

            /// <summary>
            /// This map stores information for how we optimize queries for this strategy
            /// </summary>
            static readonly Dictionary<MethodInfo, MethodInfo> OptimizingMap = new Dictionary<MethodInfo, MethodInfo> {
                { typeof(Extensions).GetMethodOrFail("GetMir"), typeof(V4StructureIndexingStrategy).GetMethodOrFail("GetMir")},
                { typeof(Extensions).GetMethodOrFail("GetMrr"), typeof(V4StructureIndexingStrategy).GetMethodOrFail("GetMrr")},
                {
                    typeof(Extensions).GetMethodOrFail("GetPcrs", typeof(IRecordContext)),
                    typeof(V4StructureIndexingStrategy).GetMethodOrFail("GetPcrs", typeof(IRecordContext))
                },
                {
                    typeof(Extensions).GetMethodOrFail("GetPcrs", typeof(IRecordContext), typeof(byte), typeof(byte)),
                    typeof(V4StructureIndexingStrategy).GetMethodOrFail("GetPcrs", typeof(IRecordContext), typeof(byte), typeof(byte))
                },
                { typeof(Extensions).GetMethodOrFail("GetSummaryPcr"), typeof(V4StructureIndexingStrategy).GetMethodOrFail("GetSummaryPcr") },
                { typeof(Extensions).GetMethodOrFail("GetPrrs"), typeof(V4StructureIndexingStrategy).GetMethodOrFail("GetPrrs") },
                { typeof(Extensions).GetMethodOrFail("GetWir"), typeof(V4StructureIndexingStrategy).GetMethodOrFail("GetWir") },
                { typeof(Extensions).GetMethodOrFail("GetWrr"), typeof(V4StructureIndexingStrategy).GetMethodOrFail("GetWrr") },
                { typeof(Extensions).GetMethodOrFail("GetPrr"), typeof(V4StructureIndexingStrategy).GetMethodOrFail("GetPrr") },
                { typeof(Extensions).GetMethodOrFail("GetPir"), typeof(V4StructureIndexingStrategy).GetMethodOrFail("GetPir") },
                { typeof(Extensions).GetMethodOrFail("GetMatchingPir"), typeof(V4StructureIndexingStrategy).GetMethodOrFail("GetMatchingPir") },
                { typeof(Extensions).GetMethodOrFail("GetMatchingPrr"), typeof(V4StructureIndexingStrategy).GetMethodOrFail("GetMatchingPrr") },
                {
                    typeof(Extensions).GetMethodOrFail("GetChildRecords", typeof(Pir)),
                    typeof(V4StructureIndexingStrategy).GetMethodOrFail("GetChildRecords", typeof(Pir))
                },
                {
                    typeof(Extensions).GetMethodOrFail("GetChildRecords", typeof(Prr)),
                    typeof(V4StructureIndexingStrategy).GetMethodOrFail("GetChildRecords", typeof(Prr))
                },
                {
                    typeof(Extensions).GetMethodOrFail("GetChildRecords_NoHeadSiteCheck", typeof(Pir)),
                    typeof(V4StructureIndexingStrategy).GetMethodOrFail("GetChildRecords_NoHeadSiteCheck", typeof(Pir))
                }
            };

            #endregion

            //some methodinfos we'll use for some tricky generic mappings
            static readonly MethodInfo OfExactTypePir = typeof(Extensions).GetMethodOrFail("OfExactType", typeof(IQueryable<StdfRecord>)).MakeGenericMethod(typeof(Pir));
            static readonly MethodInfo OfExactTypePrr = typeof(Extensions).GetMethodOrFail("OfExactType", typeof(IQueryable<StdfRecord>)).MakeGenericMethod(typeof(Prr));

            static readonly MethodInfo OptOfExactTypePir = typeof(V4StructureIndexingStrategy).GetMethodOrFail("OfExactTypePir", typeof(IQueryable<StdfRecord>));
            static readonly MethodInfo OptOfExactTypePrr = typeof(V4StructureIndexingStrategy).GetMethodOrFail("OfExactTypePrr", typeof(IQueryable<StdfRecord>));

            static readonly MethodInfo GetRecordsEnumerable = typeof(StdfFile).GetMethodOrFail("GetRecordsEnumerable");

            /// <summary>
            /// Lets us know the expression is a call to StdfFile.GetRecordsEnumerable
            /// </summary>
            static bool IsAllRecords(Expression exp) {
                if (exp is MethodCallExpression call)
                {
                    return call.Method == GetRecordsEnumerable;
                }
                return false;
            }

            /// <summary>
            /// optimizes method calls
            /// </summary>
            protected override Expression VisitMethodCall(MethodCallExpression m) {
                if (OptimizingMap.TryGetValue(m.Method, out var optimized)) {
                    //It's in the optimizing map. Replace with a call to the optimized method
                    m = Expression.Call(Expression.Constant(_Strategy), optimized, m.Arguments);
                }
                else if (m.Method == OfExactTypePir && IsAllRecords(m.Arguments[0])) {
                    //It's OfExactType<Pir>, replace with optimized method
                    m = Expression.Call(Expression.Constant(_Strategy), OptOfExactTypePir, m.Arguments);
                }
                else if (m.Method == OfExactTypePrr && IsAllRecords(m.Arguments[0])) {
                    //It's OfExactType<Prr>, replace with optimized method
                    m = Expression.Call(Expression.Constant(_Strategy), OptOfExactTypePrr, m.Arguments);
                }
                return base.VisitMethodCall(m);
            }
        }

        #region Optimized implementations

        /// <summary>
        /// Leverages the parts extents to get all Prrs.
        /// Note that this optimization gives them to you in a slightly different order if using multi-site data
        /// </summary>
        public IEnumerable<Prr> OfExactTypePrr(IEnumerable<StdfRecord> records) {
            records.Any(); //TODO: I don't remember what this is for :(  - maybe just to make sure everything is loaded to ram first?
            return from e in _PartsMap.GetAllExtents()
                   from p in GetRecordsInExtentsReverse(e).Select(r => r as Prr).TakeWhile(r => r != null)
                   select p;
        }

        /// <summary>
        /// The IQueryable implementation of OfExactTypePrr
        /// </summary>
        public IQueryable<Prr> OfExactTypePrr(IQueryable<StdfRecord> records) {
            return records.Provider.CreateQuery<Prr>(Expression.Call(Expression.Constant(this), (MethodInfo)(MethodBase.GetCurrentMethod() ?? throw new InvalidOperationException("Can't get current method")), records.Expression));
        }

        /// <summary>
        /// Leverages the parts extents to get all Pirs.
        /// Note that this optimization gives them to you in a slightly different order if using multi-site data
        /// </summary>
        public IEnumerable<Pir> OfExactTypePir(IEnumerable<StdfRecord> records)
        {
            records.Any();
            return from e in _PartsMap.GetAllExtents()
                   from p in GetRecordsInExtents(e).Select(r => r as Pir).TakeWhile(r => r != null)
                   select p;
        }

        /// <summary>
        /// The IQueryable implementation of OfExactTypePir
        /// TODO: Do we need these for all our optimizations?
        /// </summary>
        public IQueryable<Pir> OfExactTypePir(IQueryable<StdfRecord> records)
        {
            return records.Provider.CreateQuery<Pir>(Expression.Call(Expression.Constant(this), (MethodInfo)(MethodBase.GetCurrentMethod() ?? throw new InvalidOperationException("Can't get current method")), records.Expression));
        }

        /// <summary>
        /// Super fast GetMir :)
        /// </summary>
        public Mir? GetMir(IRecordContext context) {
            context.StdfFile.GetRecordsEnumerable().Any();
            return _Mir;
        }

        /// <summary>
        /// Super fast GetMrr :)
        /// </summary>
        public Mrr? GetMrr(IRecordContext context)
        {
            context.StdfFile.GetRecordsEnumerable().Any();
            return _Mrr;
        }
        /// <summary>
        /// Super fast GetPcrs :)
        /// </summary>
        public IEnumerable<Pcr> GetPcrs(IRecordContext context)
        {
            context.StdfFile.GetRecordsEnumerable().Any();
            return from p in _Pcrs select p;
        }

        /// <summary>
        /// Super fast GetPcrs :)
        /// </summary>
        public IEnumerable<Pcr> GetPcrs(IRecordContext context, byte headNumber, byte siteNumber)
        {
            context.StdfFile.GetRecordsEnumerable().Any();
            return from p in _Pcrs
                   where p.HeadNumber == headNumber
                   && p.SiteNumber == siteNumber
                   select p;
        }

        public Pcr? GetSummaryPcr(IRecordContext record) {
            record.StdfFile.GetRecordsEnumerable().Any();
            return (from p in _Pcrs
                    where p.HeadNumber == 255
                    select p).FirstOrDefault();
        }

        public IEnumerable<Prr> GetPrrs(Wrr wrr) {
            wrr.StdfFile.GetRecordsEnumerable().Any();
            //find the part extents within the wafer extent
            var waferExtent = _WafersMap.GetExtents(wrr);
            if (waferExtent is null) return Enumerable.Empty<Prr>();
            return from pe in _PartsMap.GetExtentsListWithin(waferExtent)
                   from prr in GetRecordsInExtentsReverse(pe)
                   .TakeWhile(r => r.GetType() == typeof(Prr))
                   .Select(r => (Prr)r)
                   select prr;
        }

        public Wir? GetWir(IHeadIndexable record) {
            record.StdfFile.GetRecordsEnumerable().Any();
            var waferExtent = _WafersMap.GetExtents((StdfRecord)record);
            if (waferExtent == null) return null;
            return _AllRecords?[waferExtent.StartIndex] as Wir;
        }

        public Wrr? GetWrr(IHeadIndexable record) {
            record.StdfFile.GetRecordsEnumerable().Any();
            var waferExtents = _WafersMap.GetExtents((StdfRecord)record);
            if (waferExtents == null) return null;
            return _AllRecords?[waferExtents.EndIndex] as Wrr;
        }

        public Prr? GetPrr(IHeadSiteIndexable record) {
            record.StdfFile.GetRecordsEnumerable().Any();
            var partExtents = _PartsMap.GetExtents((StdfRecord)record);
            if (partExtents == null) return null;
            return (from p in GetRecordsInExtentsReverse(partExtents)
                        .Select(p => p as Prr)
                        .TakeWhile(p => p != null)
                    where p.HeadNumber == record.HeadNumber
                    && p.SiteNumber == record.SiteNumber
                    select p).FirstOrDefault();
        }

        public Pir? GetPir(IHeadSiteIndexable record) {
            record.StdfFile.GetRecordsEnumerable().Any();
            var partExtents = _PartsMap.GetExtents((StdfRecord)record);
            if (partExtents == null) return null;
            return (from p in GetRecordsInExtents(partExtents)
                        .Select(p => p as Pir)
                        .TakeWhile(p => p != null)
                    where p.HeadNumber == record.HeadNumber
                    && p.SiteNumber == record.SiteNumber
                    select p).FirstOrDefault();
        }

        public Prr? GetMatchingPrr(Pir pir) {
            pir.StdfFile.GetRecordsEnumerable().Any();
            var partExtents = _PartsMap.GetExtents(pir);
            if (partExtents == null) return null;
            return (from p in GetRecordsInExtentsReverse(partExtents)
                        .Select(p => p as Prr)
                        .TakeWhile(p => p != null)
                    where p.HeadNumber == pir.HeadNumber
                    && p.SiteNumber == pir.SiteNumber
                    select p).FirstOrDefault();
        }

        public Pir? GetMatchingPir(Prr prr) {
            prr.StdfFile.GetRecordsEnumerable().Any();
            var partExtents = _PartsMap.GetExtents(prr);
            if (partExtents == null) return null;
            return (from p in GetRecordsInExtents(partExtents)
                        .Select(p => p as Pir)
                        .TakeWhile(p => p != null)
                    where p.HeadNumber == prr.HeadNumber
                    && p.SiteNumber == prr.SiteNumber
                    select p).FirstOrDefault();
        }

        public IEnumerable<StdfRecord> GetChildRecords(Pir pir) {
            pir.StdfFile.GetRecordsEnumerable().Any();
            var partExtents = _PartsMap.GetExtents(pir);
            if (partExtents is null) return Enumerable.Empty<StdfRecord>();
            return GetRecordsInExtents(partExtents)
                .OfType<IHeadSiteIndexable>()
                .Where(r => r.HeadNumber == pir.HeadNumber && r.SiteNumber == pir.SiteNumber)
                .TakeWhile(r => r.GetType() != typeof(Prr))
                .Cast<StdfRecord>();
        }

        public IEnumerable<StdfRecord> GetChildRecords(Prr prr) {
            prr.StdfFile.GetRecordsEnumerable().Any();
            var partExtents = _PartsMap.GetExtents(prr);
            if (partExtents is null) return Enumerable.Empty<StdfRecord>();
            return GetRecordsInExtents(partExtents)
                .SkipWhile(r => r.GetType() == typeof(Pir))
                .OfType<IHeadSiteIndexable>()
                .Where(r => r.HeadNumber == prr.HeadNumber && r.SiteNumber == prr.SiteNumber)
                .TakeWhile(r => r.GetType() != typeof(Prr))
                .Cast<StdfRecord>();
        }


        public IEnumerable<StdfRecord> GetChildRecords_NoHeadSiteCheck(Pir pir)
        {
            pir.StdfFile.GetRecordsEnumerable().Any();
            if (pir.StdfFile.IsMultiDut)
                throw new Exception("Don't use this for multidut files");
            var partExtents = _PartsMap.GetExtents(pir);
            if (partExtents is null) return Enumerable.Empty<StdfRecord>();
            return GetRecordsInExtents(partExtents)
                .TakeWhile(r => r.GetType() != typeof(Prr))
                .Cast<StdfRecord>();
        }

        #endregion
    }
}
