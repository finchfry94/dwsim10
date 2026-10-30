using System;
using System.Globalization;
using System.Linq;
using DWSIM.Automation;
using DWSIM.Bridge.Rpc;
using Newtonsoft.Json.Linq;
using unvell.ReoGrid;
using unvell.ReoGrid.Formula;

namespace DWSIM.Bridge.Methods
{
    /// <summary>
    /// The flowsheet's spreadsheet: the ReoGrid workbook the headless flowsheet keeps and saves
    /// with the file, whose GETPROPVAL/SETPROPVAL formulas read and write the simulation.
    /// </summary>
    public sealed class SpreadsheetMethods
    {
        private readonly SessionStore _sessions;

        public SpreadsheetMethods(SessionStore sessions) { _sessions = sessions; }

        private static Flowsheet2 Sheet(Session session)
            => session.Inner as Flowsheet2 ?? throw new RpcException(RpcErrorCodes.EngineError, "This flowsheet has no spreadsheet.");

        /// <summary>Recalculates every worksheet against this flowsheet; call holding the session's gate.</summary>
        internal static void Recalculate(Session session)
        {
            if (!(session.Inner is Flowsheet2 fs) || fs.SpreadsheetWorkbook == null) return;
            fs.ActivateSpreadsheetFunctions();
            foreach (var ws in fs.SpreadsheetWorkbook.Worksheets)
                if (ws.MaxContentRow >= 0 && ws.MaxContentCol >= 0) ws.Recalculate();
        }

        private static Worksheet Find(IWorkbook book, string sheet)
        {
            if (string.IsNullOrEmpty(sheet)) return book.Worksheets[0];
            return book.GetWorksheetByName(sheet) ?? throw new RpcException(RpcErrorCodes.InvalidParams, "No sheet '" + sheet + "'.");
        }

        private static JObject SheetJson(IWorkbook book, Worksheet ws)
        {
            var cells = new JArray();
            int rows = ws.MaxContentRow, cols = ws.MaxContentCol;
            if (rows >= 0 && cols >= 0)
            {
                ws.IterateCells(new RangePosition(0, 0, rows + 1, cols + 1), true, (r, c, cell) =>
                {
                    var input = cell.HasFormula ? "=" + cell.Formula : Convert.ToString(cell.Data, CultureInfo.InvariantCulture) ?? "";
                    if (input.Length == 0) return true;
                    var display = cell.DisplayText ?? "";
                    var error = cell.FormulaStatus != FormulaStatus.Normal
                        || display.StartsWith("ERROR", StringComparison.Ordinal) || display == "INVALID ARGS" || display.StartsWith("#", StringComparison.Ordinal);
                    cells.Add(new JObject
                    {
                        ["address"] = cell.Address,
                        ["row"] = r,
                        ["col"] = c,
                        ["input"] = input,
                        ["display"] = display,
                        ["value"] = cell.Data is double d ? (double.IsNaN(d) || double.IsInfinity(d) ? JValue.CreateNull() : new JValue(d)) : cell.Data == null ? JValue.CreateNull() : new JValue(Convert.ToString(cell.Data, CultureInfo.InvariantCulture)),
                        ["error"] = error
                    });
                    return true;
                });
            }
            return new JObject
            {
                ["sheets"] = new JArray(book.Worksheets.Select(w => w.Name)),
                ["sheet"] = ws.Name,
                ["rows"] = Math.Max(ws.MaxContentRow + 1, 0),
                ["cols"] = Math.Max(ws.MaxContentCol + 1, 0),
                ["cells"] = cells
            };
        }

        /// <summary>A sheet's filled cells: what was typed, what it shows, and whether it failed.</summary>
        [RpcMethod("spreadsheet.get")]
        public JObject Get(string flowsheetId, string sheet = null)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var book = Sheet(session).SpreadsheetWorkbook;
                return SheetJson(book, Find(book, sheet));
            }
        }

        /// <summary>
        /// Writes cells, each a value or a formula (starting with "="); an empty input clears the cell.
        /// The workbook is recalculated, so SETPROPVAL cells write their properties now.
        /// </summary>
        [RpcMethod("spreadsheet.set")]
        public JObject Set(string flowsheetId, JArray cells, string sheet = null)
        {
            if (cells == null || cells.Count == 0) throw new RpcException(RpcErrorCodes.InvalidParams, "Give the cells to write.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = Sheet(session);
                var ws = Find(fs.SpreadsheetWorkbook, sheet);
                var writes = cells.Select(c => (address: (c.Value<string>("address") ?? "").Trim().ToUpperInvariant(), input: c.Value<string>("input") ?? "")).ToList();
                foreach (var w in writes)
                    if (!CellPosition.IsValidAddress(w.address))
                        throw new RpcException(RpcErrorCodes.InvalidParams, "'" + w.address + "' is not a cell address.");
                session.Record(writes.Count == 1 ? "Edit cell " + writes[0].address : $"Edit {writes.Count} cells");
                fs.ActivateSpreadsheetFunctions();
                foreach (var (address, input) in writes)
                {
                    var cell = ws.Cells[address];
                    var text = input.Trim();
                    if (text.Length == 0) { cell.Formula = null; cell.Data = null; }
                    else if (text.StartsWith("=", StringComparison.Ordinal) && text.Length > 1) cell.Formula = text.Substring(1);
                    else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) { cell.Formula = null; cell.Data = number; }
                    else { cell.Formula = null; cell.Data = input; }
                }
                Recalculate(session);
                return SheetJson(fs.SpreadsheetWorkbook, ws);
            }
        }

        /// <summary>Recalculates the workbook against the flowsheet as it is now.</summary>
        [RpcMethod("spreadsheet.recalculate")]
        public JObject RecalculateNow(string flowsheetId, string sheet = null)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                Recalculate(session);
                var book = Sheet(session).SpreadsheetWorkbook;
                return SheetJson(book, Find(book, sheet));
            }
        }

        [RpcMethod("spreadsheet.addSheet")]
        public JObject AddSheet(string flowsheetId, string name = null)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var book = Sheet(session).SpreadsheetWorkbook;
                var n = book.Worksheets.Count + 1;
                var title = string.IsNullOrWhiteSpace(name) ? "Sheet" + n : name.Trim();
                while (book.GetWorksheetByName(title) != null) title = "Sheet" + ++n;
                session.Record("Add sheet " + title);
                var ws = book.CreateWorksheet(title);
                book.AddWorksheet(ws);
                return SheetJson(book, ws);
            }
        }

        [RpcMethod("spreadsheet.renameSheet")]
        public JObject RenameSheet(string flowsheetId, string sheet, string name)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var book = Sheet(session).SpreadsheetWorkbook;
                var ws = Find(book, sheet);
                var title = (name ?? "").Trim();
                if (title.Length == 0) throw new RpcException(RpcErrorCodes.InvalidParams, "Give the sheet a name.");
                if (book.GetWorksheetByName(title) is Worksheet other && other != ws)
                    throw new RpcException(RpcErrorCodes.InvalidParams, "There is already a sheet '" + title + "'.");
                session.Record("Rename sheet " + ws.Name);
                ws.Name = title;
                return SheetJson(book, ws);
            }
        }

        [RpcMethod("spreadsheet.deleteSheet")]
        public JObject DeleteSheet(string flowsheetId, string sheet)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var book = Sheet(session).SpreadsheetWorkbook;
                var ws = Find(book, sheet);
                if (book.Worksheets.Count == 1) throw new RpcException(RpcErrorCodes.InvalidParams, "The last sheet stays.");
                session.Record("Delete sheet " + ws.Name);
                book.RemoveWorksheet(ws);
                return SheetJson(book, book.Worksheets[0]);
            }
        }
    }
}
