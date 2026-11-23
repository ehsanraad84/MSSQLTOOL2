using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace MSSQLTool.Completion
{
    /// <summary>
    /// Disk seed for the completion snapshot.  A cold SSMS start used to pay the whole catalog load
    /// before the first popup; a compact file lets the popup answer immediately while the live load
    /// runs in the background.
    /// <para>
    /// What is stored: object names, kinds and column name/type/flags only.  View, procedure and
    /// function <em>definitions</em>, descriptions and default expressions are deliberately excluded —
    /// they are the bulk of the file, they carry business logic, and they are now fetched on demand.
    /// </para>
    /// <para>
    /// Every failure degrades to "no disk cache": the feature is an optimisation and must never be
    /// able to break completion.
    /// </para>
    /// </summary>
    internal static class MetadataPersistence
    {
        internal const int FormatVersion = 1;
        private const long MaxFileBytes = 64L * 1024 * 1024;
        private static string folderOverride;

        /// <summary>Test hook: points the cache at a temporary folder instead of the user data folder.</summary>
        internal static void UseFolder(string folder) => folderOverride = folder;

        internal static string Folder
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(folderOverride)) return folderOverride;
                try { return Path.Combine(AppPaths.Root, "CompletionCache"); }
                catch { return null; }
            }
        }

        internal static string FileFor(string key)
        {
            string folder = Folder;
            if (string.IsNullOrWhiteSpace(folder)) return null;
            return Path.Combine(folder, Hash(key) + ".snapshot.json");
        }

        internal static bool TryLoad(string key, out MetadataSnapshot snapshot, out MetadataSignature signature, out DateTime savedUtc)
        {
            snapshot = null;
            signature = default;
            savedUtc = DateTime.MinValue;

            string path = FileFor(key);
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= 0 || info.Length > MaxFileBytes) return false;

                SnapshotDto dto = JsonConvert.DeserializeObject<SnapshotDto>(File.ReadAllText(path, Encoding.UTF8));
                if (dto == null || dto.Format != FormatVersion) return false;
                if (!string.Equals(dto.Key, key, StringComparison.OrdinalIgnoreCase)) return false;
                if (dto.Objects == null) return false;

                savedUtc = dto.SavedUtc == default ? info.LastWriteTimeUtc : dto.SavedUtc;
                snapshot = dto.ToSnapshot(savedUtc);
                signature = dto.ToSignature();
                return snapshot.Objects.Count > 0;
            }
            catch (Exception ex)
            {
                // A truncated or foreign file is not an error worth surfacing: delete and reload.
                MSSQLToolPackage._logger?.Debug(ex, "SQL completion metadata disk cache could not be read; it will be rebuilt.");
                TryDelete(path);
                snapshot = null;
                return false;
            }
        }

        internal static void Save(string key, MetadataSnapshot snapshot, MetadataSignature signature, int maxFiles)
        {
            if (snapshot == null || snapshot.Objects.Count == 0) return;
            string path = FileFor(key);
            if (string.IsNullOrWhiteSpace(path)) return;

            string temporary = null;
            try
            {
                string folder = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(folder)) return;
                Directory.CreateDirectory(folder);

                SnapshotDto dto = SnapshotDto.From(key, snapshot, signature);
                string json = JsonConvert.SerializeObject(dto, Formatting.None);
                if (json.Length > MaxFileBytes) return;

                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, json, new UTF8Encoding(false));

                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                temporary = null;

                TrimTo(Math.Max(1, maxFiles));
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "SQL completion metadata could not be written to the disk cache.");
            }
            finally
            {
                if (temporary != null) TryDelete(temporary);
            }
        }

        internal static void Remove(string key)
        {
            string path = FileFor(key);
            if (!string.IsNullOrWhiteSpace(path)) TryDelete(path);
        }

        /// <summary>Keeps the newest files only; a roaming profile with dozens of databases must not grow forever.</summary>
        internal static void TrimTo(int maxFiles)
        {
            try
            {
                string folder = Folder;
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
                var files = new DirectoryInfo(folder).GetFiles("*.snapshot.json")
                    .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
                for (int i = Math.Max(1, maxFiles); i < files.Count; i++) TryDelete(files[i].FullName);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "SQL completion metadata disk cache could not be trimmed.");
            }
        }

        internal static long GetFolderBytes()
        {
            try
            {
                string folder = Folder;
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return 0;
                long total = 0;
                foreach (FileInfo file in new DirectoryInfo(folder).GetFiles("*.snapshot.json"))
                {
                    try { total += file.Length; } catch { }
                }
                return total;
            }
            catch { return 0; }
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private static string Hash(string key)
        {
            using (var sha = SHA1.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(key ?? string.Empty));
                var text = new StringBuilder(bytes.Length * 2);
                foreach (byte value in bytes) text.Append(value.ToString("x2"));
                return text.ToString(0, 24);
            }
        }

        // ---- on-disk shape -------------------------------------------------

        private sealed class SnapshotDto
        {
            public int Format { get; set; } = FormatVersion;
            public string Key { get; set; }
            public DateTime SavedUtc { get; set; }
            public DateTime LoadedUtc { get; set; }
            public string Scope { get; set; }
            public bool IsPartial { get; set; }
            public int CompatibilityLevel { get; set; } = 170;
            public List<string> Schemas { get; set; } = new List<string>();
            public List<string> Databases { get; set; } = new List<string>();
            public List<string> LinkedServers { get; set; } = new List<string>();
            public List<ObjectDto> Objects { get; set; } = new List<ObjectDto>();
            public List<ParameterDto> Parameters { get; set; } = new List<ParameterDto>();
            public SignatureDto Signature { get; set; }

            internal static SnapshotDto From(string key, MetadataSnapshot snapshot, MetadataSignature signature)
            {
                var dto = new SnapshotDto
                {
                    Key = key,
                    SavedUtc = DateTime.UtcNow,
                    LoadedUtc = snapshot.LoadedUtc,
                    Scope = snapshot.Scope,
                    IsPartial = snapshot.IsPartial,
                    CompatibilityLevel = snapshot.CompatibilityLevel,
                    Schemas = new List<string>(snapshot.Schemas),
                    Databases = new List<string>(snapshot.Databases),
                    LinkedServers = new List<string>(snapshot.LinkedServers),
                    Signature = signature.HasValue ? SignatureDto.From(signature) : null
                };

                foreach (DatabaseObjectMetadata item in snapshot.Objects)
                {
                    var objectDto = new ObjectDto
                    {
                        Server = item.Server,
                        Database = item.Database,
                        Schema = item.Schema,
                        Name = item.Name,
                        Kind = (int)item.Kind,
                        IsSystem = item.IsSystem,
                        IsTableValuedFunction = item.IsTableValuedFunction,
                        IsExternal = item.IsExternal,
                        SynonymBaseObjectName = item.SynonymBaseObjectName,
                        EstimatedRowCount = item.EstimatedRowCount,
                        Columns = new List<ColumnDto>(item.Columns.Count)
                    };
                    foreach (ColumnMetadata column in item.Columns)
                        objectDto.Columns.Add(new ColumnDto
                        {
                            Name = column.Name,
                            DataType = column.DataType,
                            IsNullable = column.IsNullable,
                            IsIdentity = column.IsIdentity,
                            IsComputed = column.IsComputed,
                            IsPrimaryKey = column.IsPrimaryKey,
                            IsForeignKey = column.IsForeignKey,
                            IsUnique = column.IsUnique,
                            Ordinal = column.Ordinal,
                            MaxLength = column.MaxLength,
                            Precision = column.Precision,
                            Scale = column.Scale
                        });
                    dto.Objects.Add(objectDto);
                }

                foreach (RoutineParameterMetadata parameter in snapshot.Parameters)
                    dto.Parameters.Add(new ParameterDto
                    {
                        ObjectName = parameter.ObjectName,
                        Name = parameter.Name,
                        DataType = parameter.DataType,
                        IsOutput = parameter.IsOutput,
                        MaxLength = parameter.MaxLength,
                        Precision = parameter.Precision,
                        Scale = parameter.Scale,
                        Ordinal = parameter.Ordinal,
                        HasDefaultValue = parameter.HasDefaultValue
                    });

                return dto;
            }

            internal MetadataSnapshot ToSnapshot(DateTime savedUtc)
            {
                var snapshot = new MetadataSnapshot
                {
                    LoadedUtc = LoadedUtc == default ? savedUtc : LoadedUtc,
                    PublishedUtc = savedUtc,
                    Scope = Scope,
                    IsPartial = IsPartial,
                    CompatibilityLevel = CompatibilityLevel > 0 ? CompatibilityLevel : 170,
                    Source = "disk",
                    IsStale = true
                };
                if (Schemas != null) snapshot.Schemas.AddRange(Schemas);
                if (Databases != null) snapshot.Databases.AddRange(Databases);
                if (LinkedServers != null) snapshot.LinkedServers.AddRange(LinkedServers);

                foreach (ObjectDto dto in Objects ?? new List<ObjectDto>())
                {
                    if (dto == null || string.IsNullOrWhiteSpace(dto.Name)) continue;
                    var item = new DatabaseObjectMetadata
                    {
                        Server = dto.Server,
                        Database = dto.Database,
                        Schema = dto.Schema,
                        Name = dto.Name,
                        Kind = (CompletionItemKind)dto.Kind,
                        IsSystem = dto.IsSystem,
                        IsTableValuedFunction = dto.IsTableValuedFunction,
                        IsExternal = dto.IsExternal,
                        SynonymBaseObjectName = dto.SynonymBaseObjectName,
                        EstimatedRowCount = dto.EstimatedRowCount
                    };
                    foreach (ColumnDto column in dto.Columns ?? new List<ColumnDto>())
                    {
                        if (column == null || string.IsNullOrWhiteSpace(column.Name)) continue;
                        item.Columns.Add(new ColumnMetadata
                        {
                            Name = column.Name,
                            DataType = column.DataType,
                            IsNullable = column.IsNullable,
                            IsIdentity = column.IsIdentity,
                            IsComputed = column.IsComputed,
                            IsPrimaryKey = column.IsPrimaryKey,
                            IsForeignKey = column.IsForeignKey,
                            IsUnique = column.IsUnique,
                            Ordinal = column.Ordinal,
                            MaxLength = column.MaxLength,
                            Precision = column.Precision,
                            Scale = column.Scale
                        });
                    }
                    snapshot.Objects.Add(item);
                }

                foreach (ParameterDto dto in Parameters ?? new List<ParameterDto>())
                {
                    if (dto == null || string.IsNullOrWhiteSpace(dto.ObjectName)) continue;
                    snapshot.Parameters.Add(new RoutineParameterMetadata
                    {
                        ObjectName = dto.ObjectName,
                        Name = dto.Name,
                        DataType = dto.DataType,
                        IsOutput = dto.IsOutput,
                        MaxLength = dto.MaxLength,
                        Precision = dto.Precision,
                        Scale = dto.Scale,
                        Ordinal = dto.Ordinal,
                        HasDefaultValue = dto.HasDefaultValue
                    });
                }

                return snapshot;
            }

            internal MetadataSignature ToSignature()
            {
                return Signature == null
                    ? default
                    : new MetadataSignature(Signature.Objects, Signature.ObjectIdSum, Signature.MaxModifyUtc,
                        Signature.Columns, Signature.Indexes, Signature.Constraints);
            }
        }

        private sealed class SignatureDto
        {
            public long Objects { get; set; }
            public long ObjectIdSum { get; set; }
            public DateTime MaxModifyUtc { get; set; }
            public long Columns { get; set; }
            public long Indexes { get; set; }
            public long Constraints { get; set; }

            internal static SignatureDto From(MetadataSignature signature) => new SignatureDto
            {
                Objects = signature.Objects,
                ObjectIdSum = signature.ObjectIdSum,
                MaxModifyUtc = signature.MaxModifyUtc,
                Columns = signature.Columns,
                Indexes = signature.Indexes,
                Constraints = signature.Constraints
            };
        }

        private sealed class ObjectDto
        {
            public string Server { get; set; }
            public string Database { get; set; }
            public string Schema { get; set; }
            public string Name { get; set; }
            public int Kind { get; set; }
            public bool IsSystem { get; set; }
            public bool IsTableValuedFunction { get; set; }
            public bool IsExternal { get; set; }
            public string SynonymBaseObjectName { get; set; }
            public long? EstimatedRowCount { get; set; }
            public List<ColumnDto> Columns { get; set; }
        }

        private sealed class ColumnDto
        {
            public string Name { get; set; }
            public string DataType { get; set; }
            public bool IsNullable { get; set; }
            public bool IsIdentity { get; set; }
            public bool IsComputed { get; set; }
            public bool IsPrimaryKey { get; set; }
            public bool IsForeignKey { get; set; }
            public bool IsUnique { get; set; }
            public int Ordinal { get; set; }
            public short MaxLength { get; set; }
            public byte Precision { get; set; }
            public byte Scale { get; set; }
        }

        private sealed class ParameterDto
        {
            public string ObjectName { get; set; }
            public string Name { get; set; }
            public string DataType { get; set; }
            public bool IsOutput { get; set; }
            public short MaxLength { get; set; }
            public byte Precision { get; set; }
            public byte Scale { get; set; }
            public int Ordinal { get; set; }
            public bool HasDefaultValue { get; set; }
        }
    }
}
