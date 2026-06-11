using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PgIntegrityGen
{
    internal class Program
    {
        static int Run(string[] args)
        {
            Console.WriteLine("=== PgIntegrityGen — генератор проверок целостности PostgreSQL ===\n");

            string csvPath = FindCsv(args);
            if (csvPath == null)
            {
                Console.Error.WriteLine("ОШИБКА: CSV-файл не найден.");
                Console.Error.WriteLine("Использование: PgIntegrityGen [путь_к_csv]");
                Console.Error.WriteLine("Либо положите CSV рядом с exe и запустите без аргументов.");
                return 1;
            }

            Console.WriteLine($"CSV: {csvPath}");

            List<ConstraintRow> rows;
            try
            {
                rows = ParseCsv(csvPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ОШИБКА при чтении CSV: {ex.Message}");
                return 1;
            }

            Console.WriteLine($"Загружено строк: {rows.Count}");

            string outDir = Path.Combine(Path.GetDirectoryName(csvPath)!, "pg_integrity_checks");
            Directory.CreateDirectory(outDir);
            Console.WriteLine($"Папка вывода: {outDir}\n");

            var fkAll = ExtractUniqueFks(rows);
            var fkCustom = fkAll.Where(f => f.Table.StartsWith("centrvd_", StringComparison.OrdinalIgnoreCase)).ToList();
            var allTables = rows.Select(r => r.TableName).Distinct().OrderBy(t => t).ToList();

            int fileCount = 0;
            fileCount += Write(outDir, "01_fk_integrity_check.sql", GenFkCheck(fkAll, "all"));
            fileCount += Write(outDir, "02_row_count_snapshot.sql", GenRowCount(allTables));
            fileCount += Write(outDir, "03_constraint_validation.sql", GenConstraintValidation());
            fileCount += Write(outDir, "04_amcheck.sh", GenAmcheck());
            fileCount += Write(outDir, "05_centrvd_fk_check.sql", GenFkCheck(fkCustom, "centrvd_*"));

            Console.WriteLine($"\nСтатистика:");
            Console.WriteLine($"  Всего таблиц:          {allTables.Count}");
            Console.WriteLine($"  centrvd_* таблиц:      {allTables.Count(t => t.StartsWith("centrvd_", StringComparison.OrdinalIgnoreCase))}");
            Console.WriteLine($"  sungero_* таблиц:      {allTables.Count(t => t.StartsWith("sungero_", StringComparison.OrdinalIgnoreCase))}");
            Console.WriteLine($"  NOMAD* таблиц:         {allTables.Count(t => t.StartsWith("NOMAD", StringComparison.OrdinalIgnoreCase))}");
            Console.WriteLine($"  Всего FK:              {fkAll.Count}");
            Console.WriteLine($"  centrvd_* FK:          {fkCustom.Count}");
            Console.WriteLine($"\nСгенерировано файлов:  {fileCount}");
            Console.WriteLine($"Готово. Папка: {outDir}");
            return 0;
        }

        static string? FindCsv(string[] args)
        {
            if (args.Length > 0 && File.Exists(args[0]))
                return Path.GetFullPath(args[0]);

            string exeDir = AppContext.BaseDirectory;
            string? found = Directory.EnumerateFiles(exeDir, "*.csv", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (found != null) return found;

            found = Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "*.csv", SearchOption.TopDirectoryOnly).FirstOrDefault();
            return found;
        }

        static List<ConstraintRow> ParseCsv(string path)
        {
            var result = new List<ConstraintRow>();
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length == 0) throw new InvalidDataException("Файл пустой.");

            string[] header = SplitCsvLine(lines[0]);
            int iTable = IndexOf(header, "table_name");
            int iCname = IndexOf(header, "constraint_name");
            int iCtype = IndexOf(header, "constraint_type");
            int iCol = IndexOf(header, "column_name");
            int iFtable = IndexOf(header, "foreign_table");
            int iFcol = IndexOf(header, "foreign_column");

            if (iTable < 0 || iCname < 0 || iCtype < 0)
                throw new InvalidDataException("CSV не содержит обязательных колонок (table_name, constraint_name, constraint_type).");

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                string[] cols = SplitCsvLine(lines[i]);
                result.Add(new ConstraintRow
                {
                    TableName = Get(cols, iTable),
                    ConstraintName = Get(cols, iCname),
                    ConstraintType = Get(cols, iCtype),
                    ColumnName = Get(cols, iCol),
                    ForeignTable = Get(cols, iFtable),
                    ForeignColumn = Get(cols, iFcol),
                });
            }
            return result;
        }

        static string[] SplitCsvLine(string line)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool inQuote = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuote)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (c == '"') inQuote = false;
                    else sb.Append(c);
                }
                else
                {
                    if (c == '"') inQuote = true;
                    else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                    else sb.Append(c);
                }
            }
            fields.Add(sb.ToString());
            return fields.ToArray();
        }

        static int IndexOf(string[] arr, string name) =>
            Array.FindIndex(arr, h => h.Trim('"', ' ').Equals(name, StringComparison.OrdinalIgnoreCase));

        static string Get(string[] arr, int idx) =>
            idx >= 0 && idx < arr.Length ? arr[idx].Trim() : "";

        static List<FkConstraint> ExtractUniqueFks(List<ConstraintRow> rows)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<FkConstraint>();
            foreach (var r in rows)
            {
                if (!r.ConstraintType.Equals("FOREIGN KEY", StringComparison.OrdinalIgnoreCase)) continue;
                string key = $"{r.TableName}|{r.ConstraintName}";
                if (!seen.Add(key)) continue;
                if (string.IsNullOrEmpty(r.ColumnName) || string.IsNullOrEmpty(r.ForeignTable)) continue;
                result.Add(new FkConstraint
                {
                    Table = r.TableName,
                    Column = r.ColumnName,
                    ForeignTable = r.ForeignTable,
                    ForeignColumn = string.IsNullOrEmpty(r.ForeignColumn) ? "id" : r.ForeignColumn,
                    ConstraintName = r.ConstraintName,
                });
            }
            return result;
        }

        static string Esc(string s) => s.Replace("'", "''");

        static string GenFkCheck(List<FkConstraint> fks, string scope)
        {
            var sb = new StringBuilder();
            sb.AppendLine("-- ============================================================");
            sb.AppendLine($"-- FK integrity check: поиск «осиротевших» строк");
            sb.AppendLine($"-- Scope: {scope}  |  FK-ограничений: {fks.Count}");
            sb.AppendLine("-- Запустить после миграции. Все предупреждения — нарушения.");
            sb.AppendLine("-- ============================================================");
            sb.AppendLine();
            sb.AppendLine("DO $$");
            sb.AppendLine("DECLARE");
            sb.AppendLine("  v_count   bigint;");
            sb.AppendLine("  v_errors  int := 0;");
            sb.AppendLine("BEGIN");
            sb.AppendLine();

            foreach (var fk in fks)
            {
                string msg = $"FK VIOLATION [{Esc(fk.ConstraintName)}]: " +
                             $"{Esc(fk.Table)}.{Esc(fk.Column)} -> " +
                             $"{Esc(fk.ForeignTable)}.{Esc(fk.ForeignColumn)}  " +
                             $"orphaned=%";

                sb.AppendLine($"  -- {fk.ConstraintName}");
                sb.AppendLine($"  BEGIN");
                sb.AppendLine($"    SELECT COUNT(*) INTO v_count");
                sb.AppendLine($"      FROM \"{fk.Table}\" t");
                sb.AppendLine($"      LEFT JOIN \"{fk.ForeignTable}\" p ON p.\"{fk.ForeignColumn}\" = t.\"{fk.Column}\"");
                sb.AppendLine($"      WHERE t.\"{fk.Column}\" IS NOT NULL AND p.\"{fk.ForeignColumn}\" IS NULL;");
                sb.AppendLine($"    IF v_count > 0 THEN");
                sb.AppendLine($"      RAISE WARNING '{msg}', v_count;");
                sb.AppendLine($"      v_errors := v_errors + 1;");
                sb.AppendLine($"    END IF;");
                sb.AppendLine($"  EXCEPTION WHEN others THEN");
                sb.AppendLine($"    NULL;");
                sb.AppendLine($"  END;");
                sb.AppendLine();
            }

            sb.AppendLine($"  IF v_errors = 0 THEN");
            sb.AppendLine($"    RAISE NOTICE 'OK: все {fks.Count} FK-ограничений валидны.';");
            sb.AppendLine($"  ELSE");
            sb.AppendLine($"    RAISE WARNING 'ИТОГ: найдено % нарушений FK.', v_errors;");
            sb.AppendLine($"  END IF;");
            sb.AppendLine("END $$;");
            return sb.ToString();
        }

        static string GenRowCount(List<string> tables)
        {
            var sb = new StringBuilder();
            sb.AppendLine("-- ============================================================");
            sb.AppendLine("-- Row count snapshot: сравнение количества строк после переноса");
            sb.AppendLine($"-- Таблиц: {tables.Count}");
            sb.AppendLine("-- Запустить на ИСТОЧНИКЕ и на TANTOR, сравнить результаты.");
            sb.AppendLine("-- ============================================================");
            sb.AppendLine();
            sb.AppendLine("SELECT table_name, row_count,");
            sb.AppendLine("       CASE WHEN row_count = 0 THEN 'EMPTY' ELSE 'OK' END AS status");
            sb.AppendLine("FROM (VALUES");

            for (int i = 0; i < tables.Count; i++)
            {
                string comma = i < tables.Count - 1 ? "," : "";
                sb.AppendLine($"  ('{tables[i]}', (SELECT COUNT(*) FROM \"{tables[i]}\"))  {comma}");
            }

            sb.AppendLine(") AS t(table_name, row_count)");
            sb.AppendLine("ORDER BY table_name;");
            return sb.ToString();
        }

        static string GenConstraintValidation() => @"-- ============================================================
-- Constraint validation: поиск NOT VALID ограничений
-- Пустой результат = всё в порядке.
-- ============================================================

SELECT
  c.conname  AS constraint_name,
  r.relname  AS table_name,
  CASE c.contype
    WHEN 'f' THEN 'FOREIGN KEY'
    WHEN 'u' THEN 'UNIQUE'
    WHEN 'c' THEN 'CHECK'
    WHEN 'p' THEN 'PRIMARY KEY'
  END        AS constraint_type
FROM pg_constraint c
JOIN pg_class     r ON r.oid = c.conrelid
JOIN pg_namespace n ON n.oid = r.relnamespace
WHERE NOT c.convalidated
  AND c.contype IN ('f', 'u', 'c')
  AND n.nspname NOT IN ('pg_catalog', 'information_schema')
ORDER BY r.relname, c.contype;

-- Автоматическая валидация всех NOT VALID FK (раскомментировать):
/*
DO $$
DECLARE r record;
BEGIN
  FOR r IN
    SELECT n.nspname AS schema, rel.relname AS tbl, c.conname AS con
    FROM pg_constraint c
    JOIN pg_class rel ON rel.oid = c.conrelid
    JOIN pg_namespace n ON n.oid = rel.relnamespace
    WHERE NOT c.convalidated AND c.contype = 'f'
  LOOP
    EXECUTE format('ALTER TABLE %I.%I VALIDATE CONSTRAINT %I',
                   r.schema, r.tbl, r.con);
    RAISE NOTICE 'Validated: %.% constraint %', r.schema, r.tbl, r.con;
  END LOOP;
END $$;
*/
";

        static string GenAmcheck() => @"#!/usr/bin/env bash
# ============================================================
# pg_amcheck: структурная проверка всех B-tree индексов
# Запустить от имени пользователя postgres после миграции.
# Использование: ./04_amcheck.sh [dbname] [host] [port]
# ============================================================

USERNAME=""${1:-root}""
HOST=""${2:-localhost}""
PORT=""${3:-5432}""
LOG_FILE=""/tmp/pg_amcheck_$(date +%Y%m%d_%H%M%S).log""

echo ""[$(date)] Запуск pg_amcheck на БД: $DB""
echo ""Лог: $LOG_FILE""

pg_amcheck \
  --host=""$HOST"" \
  --port=""$PORT"" \
  --username=""$USERNAME"" \
  --all \
  --install-missing \
  --jobs=4 \
  --verbose \
  2>&1 | tee ""$LOG_FILE""

STATUS=${PIPESTATUS[0]}
if [ $STATUS -eq 0 ]; then
  echo ""[OK] pg_amcheck завершился без ошибок.""
else
  echo ""[FAIL] pg_amcheck обнаружил повреждения. Смотри: $LOG_FILE""
  exit 1
fi

# Только схема centrvd (отдельный прогон для кастомного слоя):
# pg_amcheck --host=$HOST --port=$PORT --dbname=$DB \
#   --schema='centrvd_%' --all --install-missing --jobs=2
";

        static int Write(string dir, string filename, string content)
        {
            string path = Path.Combine(dir, filename);
            if (filename.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
                content = content.Replace("\r\n", "\n").Replace("\r", "\n");

            File.WriteAllText(path, content, new UTF8Encoding(false));
            Console.WriteLine($"  [+] {filename}  ({new FileInfo(path).Length / 1024.0:F1} KB)");
            return 1;
        }

        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            int code = Run(args);
            Console.ReadKey();
        }
    }

    internal record ConstraintRow
    {
        public string TableName { get; init; } = "";
        public string ConstraintName { get; init; } = "";
        public string ConstraintType { get; init; } = "";
        public string ColumnName { get; init; } = "";
        public string ForeignTable { get; init; } = "";
        public string ForeignColumn { get; init; } = "";
    }

    internal record FkConstraint
    {
        public string Table { get; init; } = "";
        public string Column { get; init; } = "";
        public string ForeignTable { get; init; } = "";
        public string ForeignColumn { get; init; } = "";
        public string ConstraintName { get; init; } = "";
    }
}
