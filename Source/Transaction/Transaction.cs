﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using EntityWorker.Core.Attributes;
using EntityWorker.Core.Helper;
using EntityWorker.Core.Interface;
using EntityWorker.Core.InterFace;
using EntityWorker.Core.Object.Library;
using FastDeepCloner;
#if NET461 || NET451 || NET46
using System.Data.SQLite;
#elif NETCOREAPP2_0 || NETSTANDARD2_0
using Microsoft.Data.Sqlite;
#endif

namespace EntityWorker.Core.Transaction
{
    public class Transaction : IRepository
    {

        private readonly DbSchema _dbSchema;

        private readonly Dictionary<string, object> _attachedObjects;

        public readonly string ConnectionString;

        public DataBaseTypes DataBaseTypes { get; private set; }

        internal DbTransaction Trans { get; private set; }

        protected DbConnection SqlConnection { get; private set; }

        private static bool _tableMigrationCheck;

        protected bool EnableMigration { get; private set; }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="connectionString">Full connection string</param>
        /// <param name="enableMigration"></param>
        /// <param name="dataBaseTypes">The type of the database Ms-sql or Sql-light</param>
        public Transaction(string connectionString, bool enableMigration, DataBaseTypes dataBaseTypes)
        {
            EnableMigration = enableMigration;
            _attachedObjects = new Dictionary<string, object>();
            if (string.IsNullOrEmpty(connectionString))
                if (string.IsNullOrEmpty(connectionString))
                    throw new Exception("connectionString cant be empty");

            ConnectionString = connectionString;
            DataBaseTypes = dataBaseTypes;
            _dbSchema = new DbSchema(this);

#if DEBUG
            _tableMigrationCheck = false;
#endif

            if (!_tableMigrationCheck && EnableMigration)
            {
                this.CreateTable<DBMigration>(false);
                this.SaveChanges();
                var ass = this.GetType().Assembly;
                IMigrationConfig config;
                if (ass.DefinedTypes.Any(a => typeof(IMigrationConfig).IsAssignableFrom(a)))
                    config = Activator.CreateInstance(ass.DefinedTypes.First(a => typeof(IMigrationConfig).IsAssignableFrom(a))) as IMigrationConfig;
                else throw new Exception("EnableMigration is true but EntityWorker could not find IMigrationConfig in the current Assembly " + ass.GetName());
                MigrationConfig(config);
            }
            _tableMigrationCheck = true;
        }

        /// <summary>
        /// Specifies the migrationConfig which contain a list Migration to migrate
        /// the migration is executed automatic but as long as you have class that inherit from IMigrationConfig
        /// or you could manually execute a migration
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <exception cref="Exception"></exception>
        protected void MigrationConfig<T>(T config) where T : IMigrationConfig
        {
            try
            {
                var migrations = config.GetMigrations(this) ?? new List<Migration>();
                this.CreateTransaction();
                foreach (var migration in migrations)
                {
                    var name = migration.GetType().FullName + migration.MigrationIdentifier;
                    var dbMigration = Get<DBMigration>().Where(x => x.Name == name).Execute();
                    if (dbMigration.Any())
                        continue;
                    var item = new DBMigration
                    {
                        Name = name,
                        DateCreated = DateTime.Now
                    };
                    migration.ExecuteMigration(this);
                    this.Save(item);
                }
                SaveChanges();
            }
            catch (Exception)
            {
                Rollback();
                throw;
            }
        }

        /// <summary>
        /// Validate Connection is Open or broken
        /// </summary>
        private void ValidateConnection()
        {
            if (SqlConnection == null)
            {
                if (DataBaseTypes == DataBaseTypes.Sqllight)
                {
#if NET461 || NET451 || NET46
                    if (SqlConnection == null)
                        SqlConnection = new SQLiteConnection(ConnectionString);
#elif NETCOREAPP2_0 || NETSTANDARD2_0
                    if (SqlConnection == null)
                        SqlConnection = new SqliteConnection(ConnectionString);
#endif
                }
                else
                {
                    SqlConnection = new SqlConnection(ConnectionString);
                }
            }

            if (SqlConnection.State == ConnectionState.Broken || SqlConnection.State == ConnectionState.Closed)
                SqlConnection.Open();
        }

        /// <summary>
        /// Create Transaction
        /// Only one Transaction will be created until it get disposed
        /// </summary>
        /// <returns></returns>
        public DbTransaction CreateTransaction()
        {
            ValidateConnection();
            if (Trans?.Connection == null)
                Trans = SqlConnection.BeginTransaction();

            return Trans;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="cmd"></param>
        /// <returns></returns>
        public IDataReader ExecuteReader(DbCommand cmd)
        {
            ValidateConnection();
            return cmd.ExecuteReader();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="cmd"></param>
        /// <returns></returns>
        public object ExecuteScalar(DbCommand cmd)
        {
            ValidateConnection();
            return cmd.ExecuteScalar();
        }

        /// <inheritdoc />
        public int ExecuteNonQuery(DbCommand cmd)
        {

            ValidateConnection();
            return cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Rollback transaction
        /// </summary>
        public void Rollback()
        {
            Trans?.Rollback();
            Dispose();
        }

        /// <summary>
        /// commit the transaction
        /// </summary>
        public void SaveChanges()
        {
            try
            {
                Trans?.Commit();
            }
            catch
            {
                Rollback();
                throw;
            }
            finally
            {
                Dispose();
            }
        }

        public virtual void Dispose()
        {
            _attachedObjects.Clear();
            Trans?.Dispose();
            SqlConnection?.Dispose();
            Trans = null;
            SqlConnection = null;
        }

        /// <summary>
        /// SqlDbType by system.Type
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public SqlDbType GetSqlType(Type type)
        {
            if (type == typeof(string))
                return SqlDbType.NVarChar;

            if (type.GetTypeInfo().IsGenericType && type.GetTypeInfo().GetGenericTypeDefinition() == typeof(Nullable<>))
                type = Nullable.GetUnderlyingType(type);
            if (type.GetTypeInfo().IsEnum)
                type = typeof(long);

            var param = new SqlParameter("", type.CreateInstance());
            return param.SqlDbType;
        }

        /// <summary>
        /// Add parameters to SqlCommand
        /// </summary>
        /// <param name="cmd"></param>
        /// <param name="attrName"></param>
        /// <param name="value"></param>
        /// <param name="dbType"></param>
        public void AddInnerParameter(DbCommand cmd, string attrName, object value, SqlDbType dbType = SqlDbType.NVarChar)
        {
            if (attrName != null && attrName[0] != '@')
                attrName = "@" + attrName;

            if (value?.GetType().GetTypeInfo().IsEnum ?? false)
                value = value.ConvertValue<long>();

            var sqlDbTypeValue = value ?? DBNull.Value;
            if (DataBaseTypes == DataBaseTypes.Mssql)
            {
                var param = new SqlParameter
                {
                    SqlDbType = dbType,
                    Value = sqlDbTypeValue,
                    ParameterName = attrName
                };
                cmd.Parameters.Add(param);
            }
            else
            {
#if NET461 || NET451 || NET46
            (cmd as SQLiteCommand).Parameters.AddWithValue(attrName, value);
#elif NETCOREAPP2_0 || NETSTANDARD2_0
                (cmd as SqliteCommand).Parameters.AddWithValue(attrName, value);
#endif
            }
        }

        /// <summary>
        /// Return SqlCommand that already contain SQLConnection
        /// </summary>
        /// <param name="sql"></param>
        /// <returns></returns>
        public DbCommand GetSqlCommand(string sql)
        {
            ValidateConnection();
            return this.ProcessSql(SqlConnection, Trans, sql);
        }

        /// <summary>
        /// return a list of LightDataTable e.g. DataSet
        /// </summary>
        /// <param name="cmd">sqlCommand that are create from GetSqlCommand</param>
        /// <param name="primaryKeyId"> Table primaryKeyId, so LightDataTable.FindByPrimaryKey could be used </param>
        /// <returns></returns>
        protected List<ILightDataTable> GetLightDataTableList(DbCommand cmd, string primaryKeyId = null)
        {
            var returnList = new List<ILightDataTable>();
            var reader = ExecuteReader(cmd);
            returnList.Add(new LightDataTable().ReadData(reader, primaryKeyId));

            while (reader.NextResult())
                returnList.Add(new LightDataTable().ReadData(reader, primaryKeyId));
            reader.Close();
            return returnList;
        }

        /// <summary>
        /// Attach object to WorkEntity to track changes
        /// </summary>
        /// <param name="objcDbEntity"></param>
        public void Attach(DbEntity objcDbEntity)
        {
            if (objcDbEntity == null)
                throw new NullReferenceException("DbEntity cant be null");
            if (objcDbEntity.Id <= 0)
                throw new NullReferenceException("Id is 0, it cant be attached");
            var key = objcDbEntity.GetEntityKey();
            if (_attachedObjects.ContainsKey(key))
                _attachedObjects.Remove(key);
            _attachedObjects.Add(key, objcDbEntity);
        }

        /// <summary>
        /// Get object changes from already attached objects
        /// </summary>
        /// <param name="entity"></param>
        /// <returns></returns>
        public Dictionary<string, object> GetObjectChanges(DbEntity entity)
        {
            var changes = new Dictionary<string, object>();
            var originalObject = _attachedObjects[entity.GetEntityKey()];
            if (originalObject == null)
                throw new Exception("Object need to be attached");
            foreach (var prop in DeepCloner.GetFastDeepClonerProperties(entity.GetType()))
            {
                var aValue = prop.GetValue(entity);
                var bValue = prop.GetValue(originalObject);
                if (!prop.IsInternalType || prop.ContainAttribute<ExcludeFromAbstract>() || (aValue == null && bValue == null))
                    continue;
                if ((aValue != null && aValue.Equals(bValue)) || (bValue != null && bValue.Equals(aValue)))
                    continue;
                changes.Add(prop.Name, prop.GetValue(entity));
            }
            return changes;
        }

        /// <summary>
        /// check if object is already attached
        /// </summary>
        /// <param name="entity"></param>
        /// <returns> primaryId >0 is mandatory </returns>
        public bool IsAttached(DbEntity entity)
        {
            return _attachedObjects.ContainsKey(entity.GetEntityKey());
        }

        /// <summary>
        /// return LightDataTable e.g. DataTable
        /// </summary>
        /// <param name="cmd">sqlCommand that are create from GetSqlCommand</param>
        /// <param name="primaryKey">Table primaryKeyId, so LightDataTable.FindByPrimaryKey could be used </param>
        /// <returns></returns>
        public ILightDataTable GetLightDataTable(DbCommand cmd, string primaryKey = null)
        {
            ValidateConnection();
            var reader = cmd.ExecuteReader();
            return new LightDataTable().ReadData(reader, primaryKey, cmd.CommandText);
        }

        #region DataBase calls

        public async Task DeleteAsync(IDbEntity entity)
        {
            await Task.Run(() =>
            {
                _dbSchema.DeleteAbstract(entity);
            });
        }

        public void Delete(IDbEntity entity)
        {
            _dbSchema.DeleteAbstract(entity);
        }

        public async Task<long> SaveAsync(IDbEntity entity)
        {
            return await Task.FromResult<long>(_dbSchema.Save(entity));
        }

        public long Save(IDbEntity entity)
        {
            return _dbSchema.Save(entity);
        }

        public async Task<IList<T>> GetAllAsync<T>() where T : class, IDbEntity
        {
            return await Task.FromResult<IList<T>>(_dbSchema.GetSqlAll(typeof(T)).Cast<T>().ToList());
        }

        public IList<T> GetAll<T>() where T : class, IDbEntity
        {
            return _dbSchema.GetSqlAll(typeof(T)).Cast<T>().ToList();
        }


        /// <summary>
        /// select by quarry
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="sqlString"></param>
        /// <returns></returns>
        public async Task<IList<T>> SelectAsync<T>(string sqlString) where T : class, IDbEntity
        {
            return await Task.FromResult<IList<T>>(_dbSchema.Select<T>(sqlString));
        }

        /// <summary>
        /// select by quarry
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="sqlString"></param>
        /// <returns></returns>
        public IList<T> Select<T>(string sqlString) where T : class, IDbEntity
        {
            return _dbSchema.Select<T>(sqlString);
        }

        public async Task LoadChildrenAsync<T>(T item, bool onlyFirstLevel, List<string> classes, List<string> ignoreList) where T : class, IDbEntity
        {
            await Task.Run(() =>
            {
                _dbSchema.LoadChildren(item, onlyFirstLevel, classes, ignoreList);
            });

        }

        public void LoadChildren<T>(T item, bool onlyFirstLevel, List<string> classes, List<string> ignoreList) where T : class, IDbEntity
        {
            _dbSchema.LoadChildren(item, onlyFirstLevel, classes, ignoreList);
        }


        public async Task LoadChildrenAsync<T, TP>(T item, bool onlyFirstLevel = false, List<string> ignoreList = null, params Expression<Func<T, TP>>[] actions) where T : class, IDbEntity
        {
            await Task.Run(() =>
            {
                var parames = new List<string>();
                if (actions != null)
                    parames = actions.ConvertExpressionToIncludeList();
                LoadChildren<T>(item, onlyFirstLevel, actions != null ? parames : null, ignoreList != null && ignoreList.Any() ? ignoreList : null);
                
            });
        }


        public void LoadChildren<T, TP>(T item, bool onlyFirstLevel = false, List<string> ignoreList = null, params Expression<Func<T, TP>>[] actions) where T : class, IDbEntity
        {
            var parames = new List<string>();
            if (actions != null)
                parames = actions.ConvertExpressionToIncludeList();
            LoadChildren<T>(item, onlyFirstLevel, actions != null ? parames : null, ignoreList != null && ignoreList.Any() ? ignoreList : null);
        }


        /// <summary>
        /// This will recreate the table and if it has a ForeignKey to other tables it will also recreate those table to
        /// use it wisely
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="repository"></param>
        /// <param name="force"> remove and recreate all</param>

        public void CreateTable<T>(bool force = false) where T : class, IDbEntity
        {
            _dbSchema.CreateTable(typeof(T), null, true, force);
        }

        /// <summary>
        /// This will recreate the table and if it has a ForeignKey to other tables it will also recreate those table to
        /// use it wisely
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="repository"></param>
        /// <param name="force"> remove and recreate all</param>

        public void CreateTable(Type type, bool force = false)
        {
            _dbSchema.CreateTable(type, null, true, force);
        }

        /// <summary>
        /// This will remove the table and if it has a ForeignKey to other tables it will also remove those table to
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="repository"></param>
        public void RemoveTable<T>() where T : class, IDbEntity
        {
            _dbSchema.RemoveTable(typeof(T));
        }

        /// <summary>
        /// This will remove the table and if it has a ForeignKey to other tables it will also remove those table to
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="repository"></param>
        /// <param name="type"></param>
        public void RemoveTable(Type type)
        {
            _dbSchema.RemoveTable(type);
        }

        public ISqlQueryable<T> Get<T>() where T : class, IDbEntity
        {
            return new SqlQueryable<T>(null, this);
        }


        #endregion
    }
}