﻿// Copyright (c) BruTile developers team. All rights reserved. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using BruTile.Predefined;
using SQLite.Net;

namespace BruTile.Cache
{
    internal class MbTilesCache : IPersistentCache<byte[]>
    {
        private static SQLiteConnectionPool _connectionPool;

        public static void SetConnectionPool(SQLiteConnectionPool connectionPool)
        {
            _connectionPool = connectionPool;
        }
        
        private const string MetadataSql = "SELECT \"value\" FROM metadata WHERE \"name\"=?;";

        private readonly SQLiteConnectionString _connectionString;

        private readonly Dictionary<string, int[]> _tileRange;
        private readonly MbTilesType _type = MbTilesType.None;
        private readonly ITileSchema _schema;
        private readonly MbTilesFormat _format;
        private readonly Extent _extent;

        internal MbTilesCache(SQLiteConnectionString connectionString, ITileSchema schema = null, MbTilesType type = MbTilesType.None)
        {
            if (_connectionPool == null)
                throw new InvalidOperationException("You must assign a platform prior to using MbTilesCache by calling MbTilesTileSource.SetPlatform()");

            _connectionString = connectionString;
            var connection = _connectionPool.GetConnection(connectionString);
            using (connection.Lock())
            {
                _type = type == MbTilesType.None ? ReadType(connection) : type;

                if (schema == null)
                {
                    // Format (if defined)
                    _format = ReadFormat(connection);

                    // Extent
                    _extent = ReadExtent(connection);


                    if (HasMapTable(connection))
                    {
                        // it is possible to override the schema by definining it in a 'map' table.
                        // This method depends on reading tiles from an 'images' table, which
                        // is not part of the MBTiles spec

                        // Declared zoom levels
                        var declaredZoomLevels = ReadZoomLevels(connection, out _tileRange);

                        // Create schema
                        _schema = new GlobalMercator(_format.ToString(), declaredZoomLevels);
                    }
                    else
                    {
                        // this is actually the most regular case:
                        _schema = new GlobalSphericalMercator();
                    }
                }
                else
                {
                    _schema = schema;
                }
            }
        }

        internal ITileSchema TileSchema { get { return _schema; }}
        internal MbTilesType Type { get { return _type; } }
        internal MbTilesFormat Format { get { return _format; } }

        private bool IsTileIndexValid(TileIndex index)
        {
            if (_tileRange == null) return true;

            // this is an optimization that makes use of an additional 'map' table which is not part of the spec
            int[] range;
            if (_tileRange.TryGetValue(index.Level, out range))
            {
                return ((range[0] <= index.Col) && (index.Col <= range[1]) &&
                        (range[2] <= index.Row) && (index.Row <= range[3]));
            }
            return false;
        }

        private static bool HasMapTable(SQLiteConnection connection)
        {
            const string sql = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='map';";
            return connection.ExecuteScalar<int>(sql) > 0;
        }

        private static Extent ReadExtent(SQLiteConnection connection)
        {
            const string sql = "SELECT \"value\" FROM metadata WHERE \"name\"=?;";
            try
            {

            var extentString = connection.ExecuteScalar<string>(sql, "bounds");
            var components = extentString.Split(',');
            var extent = new Extent(
                double.Parse(components[0], NumberFormatInfo.InvariantInfo),
                double.Parse(components[1], NumberFormatInfo.InvariantInfo),
                double.Parse(components[2], NumberFormatInfo.InvariantInfo),
                double.Parse(components[3], NumberFormatInfo.InvariantInfo)
                );

            return ToMercator(extent);
            }
            catch (Exception)
            {
                return new Extent(-20037508.342789, -20037508.342789, 20037508.342789, 20037508.342789);
            }
        }

        private static Extent ToMercator(Extent extent)
        {
            var minX = extent.MinX;
            var minY = extent.MinY;
            ToMercator(ref minX, ref minY);
            var maxX = extent.MaxX;
            var maxY = extent.MaxY;
            ToMercator(ref maxX, ref maxY);

            return new Extent(minX, minY, maxX, maxY);
        }

        private static void ToMercator(ref double mercatorX_lon, ref double mercatorY_lat)
        {
            if ((Math.Abs(mercatorX_lon) > 180 || Math.Abs(mercatorY_lat) > 90))
                return;

            double num = mercatorX_lon * 0.017453292519943295;
            double x = 6378137.0 * num;
            double a = mercatorY_lat * 0.017453292519943295;

            mercatorX_lon = x;
            mercatorY_lat = 3189068.5 * Math.Log((1.0 + Math.Sin(a)) / (1.0 - Math.Sin(a)));
        }



        private static int[] ReadZoomLevels(SQLiteConnection connection, out Dictionary<string, int[]> tileRange)
        {
            var zoomLevels = new List<int>();
            tileRange = new Dictionary<string, int[]>();

                //Hack to see if "tiles" is a view
                var sql = "SELECT count(*) FROM sqlite_master WHERE type = 'view' AND name = 'tiles';";
                var name = "tiles";
                if (connection.ExecuteScalar<int>(sql) == 1)
                {
                    //Hack to choose the index table
                    sql = "SELECT sql FROM sqlite_master WHERE type = 'view' AND name = 'tiles';";
                    var sqlCreate = connection.ExecuteScalar<string>(sql);
                    if (!string.IsNullOrEmpty(sqlCreate))
                    {
                        sql = sql.Replace("\n", "");
                        var indexFrom = sql.IndexOf(" FROM ", StringComparison.OrdinalIgnoreCase) + 6;
                        var indexJoin = sql.IndexOf(" INNER ", StringComparison.OrdinalIgnoreCase);
                        if (indexJoin == -1)
                            indexJoin = sql.IndexOf(" JOIN ", StringComparison.OrdinalIgnoreCase);
                        if (indexJoin > indexFrom)
                        {
                            sql = sql.Substring(indexFrom, indexJoin - indexFrom).Trim();
                            name = sql.Replace("\"", "");
                        }
                    }
                }

                sql = "select \"zoom_level\", " +
                      "min(\"tile_column\") AS tc_min, max(\"tile_column\") AS tc_max, " +
                      "min(\"tile_row\") AS tr_min, max(\"tile_row\") AS tr_max " +
                      "from \"" + name + "\" group by \"zoom_level\";";

            var zlminmax = connection.Query<ZoomLevelMinMax>(sql);
            if (zlminmax == null || zlminmax.Count == 0)
                throw new Exception("No data in MbTiles");

            foreach (var tmp in zlminmax)
            {
                var zlString = tmp.ZoomLevel.ToString(NumberFormatInfo.InvariantInfo);
                zoomLevels.Add(tmp.ZoomLevel);
                tileRange.Add(tmp.ZoomLevel.ToString(NumberFormatInfo.InvariantInfo), new[]
                {
                    tmp.TileColMin, tmp.TileColMax,
                    tmp.TileRowMin, tmp.TileRowMax
                });
            }

            return zoomLevels.ToArray();
        }

        private static MbTilesFormat ReadFormat(SQLiteConnection connection)
        {
            try
            {
                var formatString = connection.ExecuteScalar<string>(MetadataSql, "format");
                var format = (MbTilesFormat)Enum.Parse(typeof(MbTilesFormat), formatString, true);
                return format;
            }
            catch { }
            return MbTilesFormat.Png;
        }

        private static MbTilesType ReadType(SQLiteConnection connection)
        {
            try
            {
                var typeString = connection.ExecuteScalar<string>(MetadataSql, "type");
                var type = (MbTilesType)Enum.Parse(typeof(MbTilesType), typeString, true);
                return type;
            }
            catch { }
            return MbTilesType.BaseLayer;
        }

        /*
        public int[] DeclaredZoomLevels { get { return _declaredZoomLevels; } }
        */

        internal static Extent MbTilesFullExtent { get { return new Extent(-180, -85, 180, 85); } }

        //internal static void Create(string connectionString,
        //    string name, MbTilesType type, double version, string description,
        //    MbTilesFormat format, Extent extent, params string[] kvp)
        //{
        //    var dict = new Dictionary<string, string>();
        //    if (!string.IsNullOrEmpty(name)) dict.Add("name", name);
        //    dict.Add("type", type.ToString().ToLowerInvariant());
        //    dict.Add("version", version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        //    if (!string.IsNullOrEmpty(description)) dict.Add("description", description);
        //    dict.Add("format", format.ToString().ToLower());
        //    dict.Add("bounds", string.Format(System.Globalization.NumberFormatInfo.InvariantInfo,
        //        "{0},{1},{2},{3}", extent.MinX, extent.MinY, extent.MaxX, extent.MaxY));

        //    for (var i = 0; i < kvp.Length - 1; i++)
        //        dict.Add(kvp[i++], kvp[i]);
        //}


        public void Add(TileIndex index, byte[] tile)
        {
            throw new NotSupportedException("MbTilesCache is a read-only cache");
        }

        public void Remove(TileIndex index)
        {
            throw new NotSupportedException("MbTilesCache is a read-only cache");
        }

        public byte[] Find(TileIndex index)
        {
            if (IsTileIndexValid(index))
            {
                byte[] result;
                var cn = _connectionPool.GetConnection(_connectionString);
                using(cn.Lock())
                {
                    const string sql =
                        "SELECT tile_data FROM \"tiles\" WHERE zoom_level=? AND tile_row=? AND tile_column=?;";
                    result = cn.ExecuteScalar<byte[]>(sql, int.Parse(index.Level), index.Row, index.Col);
                }
                return result == null || result.Length == 0
                    ? null
                    : result;
            }
            return null;
        }

        /// <summary>
        /// Gets the extent covered in WebMercator
        /// </summary>
        public Extent Extent
        {
            get { return _extent; }
        }

    }

    [SQLite.Net.Attributes.Table("tiles")]
    internal class TileRecord
    {
        [SQLite.Net.Attributes.Column("zoom_level")] 
        public int ZoomLevel { get; set; }
        [SQLite.Net.Attributes.Column("tile_row")]
        public int TileRow { get; set; }
        [SQLite.Net.Attributes.Column("tile_column")]
        public int TileCol { get; set; }
        [SQLite.Net.Attributes.Column("tile_data")]
        public byte[] TileData { get; set; }
    }

    [SQLite.Net.Attributes.Table("tiles")]
    internal class ZoomLevelMinMax
    {
        [SQLite.Net.Attributes.Column("zoom_level")]
        public int ZoomLevel { get; set; }
        [SQLite.Net.Attributes.Column("tr_min")]
        public int TileRowMin { get; set; }
        [SQLite.Net.Attributes.Column("tr_max")]
        public int TileRowMax { get; set; }
        [SQLite.Net.Attributes.Column("tc_min")]
        public int TileColMin { get; set; }
        [SQLite.Net.Attributes.Column("tc_max")]
        public int TileColMax { get; set; }
    }
//    internal class MbTilesCache : DbCache<SQLiteConnection>, ISerializable
//    {
//        //private static DbCommand AddTileCommand(DbConnection connection,
//        //    DecorateDbObjects qualifier, String schema, String table, char parameterPrefix = ':')
//        //{
//        //    /*
//        //    DbCommand cmd = connection.CreateCommand();
//        //    cmd.CommandText = String.Format(
//        //        "INSERT INTO {0} VALUES(:Level, :Col, :Row, :Image);", qualifier(schema, table));

//        //    DbParameter par = cmd.CreateParameter();
//        //    par.DbType = DbType.Int32;
//        //    par.ParameterName = "Level";
//        //    cmd.Parameters.Add(par);

//        //    par = cmd.CreateParameter();
//        //    par.DbType = DbType.Int32;
//        //    par.ParameterName = "Row";
//        //    cmd.Parameters.Add(par);

//        //    par = cmd.CreateParameter();
//        //    par.DbType = DbType.Int32;
//        //    par.ParameterName = "Col";
//        //    cmd.Parameters.Add(par);

//        //    par = cmd.CreateParameter();
//        //    par.DbType = DbType.Binary;
//        //    par.ParameterName = "Image";
//        //    cmd.Parameters.Add(par);

//        //    return cmd;
//        //    */
//        //    throw new InvalidOperationException("Removing tiles from MbTiles is not allowed");
//        //}

//        //private static DbCommand RemoveTileCommand(DbConnection connection,
//        //    DecorateDbObjects qualifier, String schema, String table, char parameterPrefix = ':')
//        //{
//        //    /*
//        //    DbCommand cmd = connection.CreateCommand();
//        //    cmd.CommandText = string.Format("DELETE FROM {0} WHERE ({1}=:Level AND {3}=:Col AND {2}=:Row);",
//        //        qualifier(schema, table), qualifier(table, "zoom_level"), qualifier(table, "tile_row"),
//        //        qualifier(table, "tile_col"));

//        //    DbParameter par = cmd.CreateParameter();
//        //    par.DbType = DbType.Int32;
//        //    par.ParameterName = "Level";
//        //    cmd.Parameters.Add(par);

//        //    par = cmd.CreateParameter();
//        //    par.DbType = DbType.Int32;
//        //    par.ParameterName = "Row";
//        //    cmd.Parameters.Add(par);

//        //    par = cmd.CreateParameter();
//        //    par.DbType = DbType.Int32;
//        //    par.ParameterName = "Col";
//        //    cmd.Parameters.Add(par);

//        //    return cmd;
//        //     */
//        //    throw new InvalidOperationException("Removing tiles from MbTiles is not allowed");
//        //}

//        private static DbCommand FindTileCommand(DbConnection connection,
//            DecorateDbObjects qualifier, String schema, String table, char parameterPrefix = ':')
//        {
//            var cmd = connection.CreateCommand();
//            cmd.CommandText = String.Format("SELECT {0} FROM {1} WHERE ({2}={5}Level AND {4}={5}Col AND {3}={5}Row);",
//                qualifier(table, "tile_data"), qualifier(schema, table), qualifier(table, "zoom_level"), qualifier(table, "tile_row"),
//                qualifier(table, "tile_column"), parameterPrefix);

//            DbParameter par = cmd.CreateParameter();
//            par.DbType = DbType.Int32;
//            par.ParameterName = "Level";
//            cmd.Parameters.Add(par);

//            par = cmd.CreateParameter();
//            par.DbType = DbType.Int32;
//            par.ParameterName = "Row";
//            cmd.Parameters.Add(par);

//            par = cmd.CreateParameter();
//            par.DbType = DbType.Int32;
//            par.ParameterName = "Col";
//            cmd.Parameters.Add(par);

//            return cmd;
//        }

//        private readonly int[] _declaredZoomLevels;
//        private readonly Dictionary<string, int[]> _tileRange;

//        private readonly ITileSchema _schema;

//        public MbTilesCache(SQLiteConnection connection, ITileSchema schema = null, MbTilesType type = MbTilesType.None)
//            : base(connection, (parent, child) => string.Format("\"{0}\"", child), string.Empty, "tiles",
//            null, null, FindTileCommand)
//        {
//            var wasOpen = true;
//            if (Connection.State != ConnectionState.Open)
//            {
//                Connection.Open();
//                wasOpen = false;
//            }
            
//            if (type == MbTilesType.None)
//            {
//                // Type (if defined)
//                _type = ReadType(connection);
//            }
//            else
//            {
//                _type = type;
//            }

//            if (schema == null)
//            {
//                // Format (if defined)
//                _format = ReadFormat(connection);

//                // Extent
//                _extent = ReadExtent(connection);

                
//                if (HasMapTable(connection))
//                {
//                    // it is possible to override the schema by definining it in a 'map' table.
//                    // This method depends on reading tiles from an 'images' table, which
//                    // is not part of the MBTiles spec
                    
//                    // Declared zoom levels
//                    _declaredZoomLevels = ReadZoomLevels(connection, out _tileRange);

//                    // Create schema
//                    _schema = new GlobalMercator(Format.ToString(), _declaredZoomLevels);
//                }
//                else
//                {
//                    // this is actually the most regular case:
//                    _schema = new GlobalSphericalMercator();
//                }
//            }
//            else
//            {
//                _schema = schema;
//            }

//            if (!wasOpen)
//                Connection.Close();
//        }

//        public MbTilesCache(SerializationInfo info, StreamingContext context)
//            :this(ConnectionFromInfo(info), SchemaFromInfo(info), MbTilesTypeFromInfo(info))
//        {
            
//        }

//        private static SQLiteConnection ConnectionFromInfo(SerializationInfo info)
//        {
//            var connectionString = info.GetString("connectionString");
//            return new SQLiteConnection(connectionString);
//        }

//        private static ITileSchema SchemaFromInfo(SerializationInfo info)
//        {
//            var schemaType = (Type)info.GetValue("schemaType", typeof (Type));
//            var schema = (ITileSchema)info.GetValue("schema", schemaType);
//            return schema;
//        }

//        private static MbTilesType MbTilesTypeFromInfo(SerializationInfo info)
//        {
//            return (MbTilesType)info.GetInt32("mbTilesType");
//        }

//        private static bool HasMapTable(SQLiteConnection connection)
//        {
//            if (connection.State != ConnectionState.Open)
//                connection.Open();


//            using (var cmd = connection.CreateCommand())
//            {
//                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='map';";
//                var reader = cmd.ExecuteReader(CommandBehavior.SingleRow);
//                return reader.HasRows;
//            }
//        }

//        private static Extent ReadExtent(SQLiteConnection connection)
//        {
//            if (connection.State != ConnectionState.Open)
//                connection.Open();

//            using (var cmd = connection.CreateCommand())
//            {
//                cmd.CommandText = "SELECT \"value\" FROM metadata WHERE \"name\"=:PName;";
//                cmd.Parameters.Add(new SQLiteParameter("PName", DbType.String) { Value = "bounds" });
//                var reader = cmd.ExecuteReader(CommandBehavior.SingleRow);
//                if (reader.HasRows)
//                {
//                    reader.Read();
//                    try
//                    {
//                        var extentString = reader.GetString(0);
//                        var components = extentString.Split(',');
//                        var extent = new Extent(
//                            double.Parse(components[0], System.Globalization.NumberFormatInfo.InvariantInfo),
//                            double.Parse(components[1], System.Globalization.NumberFormatInfo.InvariantInfo),
//                            double.Parse(components[2], System.Globalization.NumberFormatInfo.InvariantInfo),
//                            double.Parse(components[3], System.Globalization.NumberFormatInfo.InvariantInfo)
//                            );

//                        return ToMercator(extent);

//                    }
//                    catch { }
//                    /*
//                    if (Enum.TryParse(reader.GetString(0), true, out format))
//                        Format = format;
//                        */
//                }
//            }
//            return new Extent(-20037508.342789, -20037508.342789, 20037508.342789, 20037508.342789);
//        }

//        private static Extent ToMercator(Extent extent)
//        {
//            // todo: convert to mercator
//            return extent;
//        }

//        private static int[] ReadZoomLevels(SQLiteConnection connection, out Dictionary<string, int[]> tileRange)
//        {
//            if (connection.State != ConnectionState.Open)
//                connection.Open();

//            var zoomLevels = new List<int>();
//            tileRange = new Dictionary<string, int[]>();

//            using (var cmd = connection.CreateCommand())
//            {
//                //Hack to see if "tiles" is a view
//                cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'view' AND name = 'tiles';";
//                var name = "tiles";
//                if (Convert.ToInt32(cmd.ExecuteScalar()) == 1)
//                {
//                    //Hack to choose the index table
//                    cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'view' AND name = 'tiles';";
//                    var sql = (string)cmd.ExecuteScalar();
//                    if (!string.IsNullOrEmpty(sql))
//                    {
//                        sql = sql.Replace("\n", "");
//                        var indexFrom = sql.IndexOf(" FROM ", StringComparison.InvariantCultureIgnoreCase) + 6;
//                        var indexJoin = sql.IndexOf(" INNER ", StringComparison.InvariantCultureIgnoreCase);
//                        if (indexJoin == -1)
//                            indexJoin = sql.IndexOf(" JOIN ", StringComparison.InvariantCultureIgnoreCase);
//                        if (indexJoin > indexFrom)
//                        {
//                            sql = sql.Substring(indexFrom, indexJoin - indexFrom).Trim();
//                            name = sql.Replace("\"", "");
//                        }
//                    }
//                }

//                cmd.CommandText =
//                    "select \"zoom_level\", " +
//                            "min(\"tile_column\"), max(\"tile_column\"), " +
//                            "min(\"tile_row\"), max(\"tile_row\") " +
//                            "from \"" + name + "\" group by \"zoom_level\";";

//                var reader = cmd.ExecuteReader();
//                if (reader.HasRows)
//                {
//                    while (reader.Read())
//                    {
//                        var zoomLevel = reader.GetInt32(0);
//                        zoomLevels.Add(zoomLevel);
//                        tileRange.Add(zoomLevel.ToString(CultureInfo.InvariantCulture), new[] { reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4) });
//                    }
//                }
//                if (zoomLevels.Count == 0)
//                {
//                    throw new Exception("No data in MbTiles");
//                }
//            }
//            return zoomLevels.ToArray();
//        }

//        private static MbTilesFormat ReadFormat(SQLiteConnection connection)
//        {
//            if (connection.State != ConnectionState.Open)
//                connection.Open();

//            using (var cmd = connection.CreateCommand())
//            {
//                cmd.CommandText = "SELECT \"value\" FROM metadata WHERE \"name\"=:PName;";
//                cmd.Parameters.Add(new SQLiteParameter("PName", DbType.String) { Value = "format" });
//                var reader = cmd.ExecuteReader(CommandBehavior.SingleRow);
//                if (reader.HasRows)
//                {
//                    reader.Read();
//                    try
//                    {
//                        var format = (MbTilesFormat)Enum.Parse(typeof(MbTilesFormat), reader.GetString(0), true);
//                        return format;
//                    }
//                    catch { }
//                    /*
//                    if (Enum.TryParse(reader.GetString(0), true, out format))
//                        Format = format;
//                        */
//                }
//            }
//            return MbTilesFormat.Png;
//        }

//        private static MbTilesType ReadType(SQLiteConnection connection)
//        {
//            if (connection.State != ConnectionState.Open)
//                connection.Open();

//            using (var cmd = connection.CreateCommand())
//            {
//                cmd.CommandText = "SELECT \"value\" FROM metadata WHERE \"name\"=:PName;";
//                cmd.Parameters.Add(new SQLiteParameter("PName", DbType.String) { Value = "type" });
//                var reader = cmd.ExecuteReader(CommandBehavior.SingleRow);
//                if (reader.HasRows)
//                {
//                    reader.Read();
//                    try
//                    {
//                        var type = (MbTilesType)Enum.Parse(typeof(MbTilesType), reader.GetString(0), true);
//                        return type;
//                    }
//                    catch { }
//                    /*
//                    if (Enum.TryParse(reader.GetString(0), true, out format))
//                        Format = format;
//                        */
//                }
//            }
//            return MbTilesType.BaseLayer;
//        }

//        /*
//        public int[] DeclaredZoomLevels { get { return _declaredZoomLevels; } }
//        */

//        internal static Extent MbTilesFullExtent { get { return new Extent(-180, -85, 180, 85); } }

//        internal static void Create(string connectionString,
//            string name, MbTilesType type, double version, string description,
//            MbTilesFormat format, Extent extent, params string[] kvp)
//        {
//            var dict = new Dictionary<string, string>();
//            if (!string.IsNullOrEmpty(name)) dict.Add("name", name);
//            dict.Add("type", type.ToString().ToLowerInvariant());
//            dict.Add("version", version.ToString(System.Globalization.CultureInfo.InvariantCulture));
//            if (!string.IsNullOrEmpty(description)) dict.Add("description", description);
//            dict.Add("format", format.ToString().ToLower());
//            dict.Add("bounds", string.Format(System.Globalization.NumberFormatInfo.InvariantInfo,
//                "{0},{1},{2},{3}", extent.MinX, extent.MinY, extent.MaxX, extent.MaxY));

//            for (var i = 0; i < kvp.Length - 1; i++)
//                dict.Add(kvp[i++], kvp[i]);
//        }

//#if !(SILVERLIGHT || WINDOWS_PHONE)

//        internal static void Create(string connectionString, IDictionary<string, string> metadata)
//        {
//            var csb = new SQLiteConnectionStringBuilder(connectionString);
//            if (File.Exists(csb.DataSource))
//                File.Delete(csb.DataSource);

//            using (var cn = new SQLiteConnection(connectionString))
//            {
//                cn.Open();
//                using (var cmd = cn.CreateCommand())
//                {
//                    cmd.CommandText =
//                        "CREATE TABLE metadata (name text, value text);" +
//                        "CREATE TABLE tiles (zoom_level integer, tile_column integer, tile_row integer, tile_data blob);" +
//                        "CREATE UNIQUE INDEX idx_tiles ON tiles (zoom_level, tile_colum, tile_row);";
//                    cmd.ExecuteNonQuery();

//                    cmd.CommandText = "INSERT INTO metadata VALUES (?, ?);";
//                    var pName = new SQLiteParameter("PName", DbType.String); cmd.Parameters.Add(pName);
//                    var pValue = new SQLiteParameter("PValue", DbType.String); cmd.Parameters.Add(pValue);

//                    if (metadata == null || metadata.Count == 0)
//                    {
//                        metadata = new Dictionary<string, string>();
//                    }
//                    if (!metadata.ContainsKey("bounds"))
//                        metadata.Add("bounds", "-180,-85,180,85");

//                    foreach (var kvp in metadata)
//                    {
//                        pName.Value = kvp.Key;
//                        pValue.Value = kvp.Value;
//                        cmd.ExecuteNonQuery();
//                    }
//                }
//            }
//        }

//#endif

//        protected override bool IsTileIndexValid(TileIndex index)
//        {
//            if (_tileRange == null) return true;

//            // this is an optimization that makes use of an additional 'map' table which is not part of the spec
//            int[] range;
//            if (_tileRange.TryGetValue(index.Level, out range))
//            {
//                return ((range[0] <= index.Col) && (index.Col <= range[1]) &&
//                        (range[2] <= index.Row) && (index.Row <= range[3]));
//            }
//            return false;
//        }

//        protected override byte[] GetBytes(IDataReader reader)
//        {
//            byte[] ret = null;
//            if (reader.Read())
//            {
//                if (!reader.IsDBNull(0))
//                    ret = (byte[])reader.GetValue(0);
//            }
//            return ret;
//        }

//        internal ITileSchema TileSchema
//        {
//            get { return _schema; }
//        }

//        private readonly MbTilesFormat _format;
//        private readonly MbTilesType _type;
//        private readonly Extent _extent;

//        public MbTilesFormat Format
//        {
//            get { return _format; }
//        }

//        public MbTilesType Type
//        {
//            get { return _type; }
//        }

//        public Extent Extent
//        {
//            get { return _extent; }
//        }

//        void ISerializable.GetObjectData(SerializationInfo info, StreamingContext context)
//        {
//            info.AddValue("connectionString", Connection.ConnectionString);
//            info.AddValue("schemaType", _schema.GetType());
//            info.AddValue("schema", _schema);
//            info.AddValue("mbTilesType", (int)_type);
//        }
//    }
}