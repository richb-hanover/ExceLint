﻿using System.IO;
using System.Collections.Generic;
using System.Linq;
using CsvHelper;

namespace ExceLintFileFormats
{
    public struct BugAnnotation
    {
        public BugKind BugKind;
        public string Note;

        public BugAnnotation(BugKind bugKind, string note)
        {
            BugKind = bugKind;
            Note = note;
        }

        public string Comment
        {
            get { return BugKind.ToString() + ": " + Note; }
        }
    };

    public class ExceLintGroundTruth
    {
        private string _dbpath;
        private Dictionary<AST.Address, BugKind> _bugs = new Dictionary<AST.Address, BugKind>();
        private Dictionary<AST.Address, string> _notes = new Dictionary<AST.Address, string>();

        private AST.Address Address(string addrStr, string worksheetName, string workbookName)
        {
            return AST.Address.FromA1String(
                addrStr.ToUpper(),
                worksheetName,
                workbookName,
                ""  // we don't care about paths
            );
        }

        private ExceLintGroundTruth(string dbpath, ExceLintGroundTruthRow[] rows)
        {
            _dbpath = dbpath;

            foreach (var row in rows)
            {
                AST.Address addr = Address(row.Address, row.Worksheet, row.Workbook);
                _bugs.Add(addr, BugKind.ToKind(row.BugKind));
                _notes.Add(addr, row.Notes);
            }
        }

        public BugAnnotation AnnotationFor(AST.Address addr)
        {
            if (_bugs.ContainsKey(addr))
            {
                return new BugAnnotation(_bugs[addr], _notes[addr]);
            } else
            {
                return new BugAnnotation(BugKind.NotABug, "");
            }
        }

        public List<System.Tuple<AST.Address,BugAnnotation>> AnnotationsFor(string workbookname)
        {
            var output = new List<System.Tuple<AST.Address, BugAnnotation>>();

            foreach (var bug in _bugs)
            {
                var addr = bug.Key;

                if (addr.WorkbookName == workbookname)
                {
                    output.Add(new System.Tuple<AST.Address, BugAnnotation>(addr, new BugAnnotation(bug.Value, _notes[bug.Key])));
                }
            }

            return output;
        }

        public void SetAnnotationFor(AST.Address addr, BugAnnotation annot)
        {
            if (_bugs.ContainsKey(addr))
            {
                _bugs[addr] = annot.BugKind;
                _notes[addr] = annot.Note;
            }
            else
            {
                _bugs.Add(addr, annot.BugKind);
                _notes.Add(addr, annot.Note);
            }
        }

        public void Write()
        {
            using (StreamWriter sw = new StreamWriter(_dbpath))
            {
                using (CsvWriter cw = new CsvWriter(sw))
                {
                    cw.WriteHeader<ExceLintGroundTruthRow>();

                    foreach(var pair in _bugs)
                    {
                        var row = new ExceLintGroundTruthRow();
                        row.Address = pair.Key.A1Local();
                        row.Worksheet = pair.Key.A1Worksheet();
                        row.Workbook = pair.Key.A1Workbook();
                        row.BugKind = pair.Value.ToLog();
                        row.Notes = _notes[pair.Key];

                        cw.WriteRecord(row);
                    }
                }
            }
        }

        public bool IsABug(AST.Address addr)
        {
            return _bugs.ContainsKey(addr) && _bugs[addr] != BugKind.NotABug;
        }

        public HashSet<AST.Address> TrueRefBugsByWorkbook(string wbname)
        {
            return new HashSet<AST.Address>(
                _bugs
                    .Where(pair => pair.Key.A1Workbook() == wbname)
                    .Select(pair => pair.Key)
                );
        }

        public static ExceLintGroundTruth Load(string path)
        {
            using (var sr = new StreamReader(path))
            {
                var rows = new CsvReader(sr).GetRecords<ExceLintGroundTruthRow>().ToArray();

                return new ExceLintGroundTruth(path, rows);
            }
        }

        public static ExceLintGroundTruth Create(string gtpath)
        {
            using (StreamWriter sw = new StreamWriter(gtpath))
            {
                using (CsvWriter cw = new CsvWriter(sw))
                {
                    cw.WriteHeader<ExceLintGroundTruthRow>();
                }
            }

            return Load(gtpath);
        }
    }

    class ExceLintGroundTruthRow
    {
        public string Workbook { get; set; }
        public string Worksheet { get; set; }
        public string Address { get; set; }
        public string BugKind { get; set; }
        public string Notes { get; set; }
    }
}
