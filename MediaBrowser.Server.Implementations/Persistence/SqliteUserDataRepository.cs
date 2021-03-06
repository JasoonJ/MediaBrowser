﻿using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Concurrent;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Server.Implementations.Persistence
{
    public class SqliteUserDataRepository : IUserDataRepository
    {
        private readonly ILogger _logger;
        
        private readonly ConcurrentDictionary<string, UserItemData> _userData = new ConcurrentDictionary<string, UserItemData>();

        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        private SQLiteConnection _connection;
        
        /// <summary>
        /// Gets the name of the repository
        /// </summary>
        /// <value>The name.</value>
        public string Name
        {
            get
            {
                return "SQLite";
            }
        }

        private readonly IJsonSerializer _jsonSerializer;

        /// <summary>
        /// The _app paths
        /// </summary>
        private readonly IApplicationPaths _appPaths;

        /// <summary>
        /// Initializes a new instance of the <see cref="SqliteUserDataRepository"/> class.
        /// </summary>
        /// <param name="appPaths">The app paths.</param>
        /// <param name="jsonSerializer">The json serializer.</param>
        /// <param name="logManager">The log manager.</param>
        /// <exception cref="System.ArgumentNullException">
        /// jsonSerializer
        /// or
        /// appPaths
        /// </exception>
        public SqliteUserDataRepository(IApplicationPaths appPaths, IJsonSerializer jsonSerializer, ILogManager logManager)
        {
            if (jsonSerializer == null)
            {
                throw new ArgumentNullException("jsonSerializer");
            }
            if (appPaths == null)
            {
                throw new ArgumentNullException("appPaths");
            }

            _jsonSerializer = jsonSerializer;
            _appPaths = appPaths;
            _logger = logManager.GetLogger(GetType().Name);
        }

        /// <summary>
        /// Opens the connection to the database
        /// </summary>
        /// <returns>Task.</returns>
        public async Task Initialize()
        {
            var dbFile = Path.Combine(_appPaths.DataPath, "userdata.db");

            _connection = await SqliteExtensions.ConnectToDb(dbFile).ConfigureAwait(false);

            string[] queries = {

                                "create table if not exists userdata (key nvarchar, userId GUID, data BLOB)",
                                "create unique index if not exists userdataindex on userdata (key, userId)",
                                "create table if not exists schema_version (table_name primary key, version)",
                                //pragmas
                                "pragma temp_store = memory"
                               };

            _connection.RunQueries(queries, _logger);
        }

        /// <summary>
        /// Saves the user data.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <param name="key">The key.</param>
        /// <param name="userData">The user data.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        /// <exception cref="System.ArgumentNullException">userData
        /// or
        /// cancellationToken
        /// or
        /// userId
        /// or
        /// userDataId</exception>
        public async Task SaveUserData(Guid userId, string key, UserItemData userData, CancellationToken cancellationToken)
        {
            if (userData == null)
            {
                throw new ArgumentNullException("userData");
            }
            if (cancellationToken == null)
            {
                throw new ArgumentNullException("cancellationToken");
            }
            if (userId == Guid.Empty)
            {
                throw new ArgumentNullException("userId");
            }
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentNullException("key");
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await PersistUserData(userId, key, userData, cancellationToken).ConfigureAwait(false);

                var newValue = userData;

                // Once it succeeds, put it into the dictionary to make it available to everyone else
                _userData.AddOrUpdate(GetInternalKey(userId, key), newValue, delegate { return newValue; });
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error saving user data", ex);

                throw;
            }
        }

        /// <summary>
        /// Gets the internal key.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <param name="key">The key.</param>
        /// <returns>System.String.</returns>
        private string GetInternalKey(Guid userId, string key)
        {
            return userId + key;
        }

        /// <summary>
        /// Persists the user data.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <param name="key">The key.</param>
        /// <param name="userData">The user data.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        public async Task PersistUserData(Guid userId, string key, UserItemData userData, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var serialized = _jsonSerializer.SerializeToBytes(userData);

            cancellationToken.ThrowIfCancellationRequested();

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            SQLiteTransaction transaction = null;

            try
            {
                transaction = _connection.BeginTransaction();

                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "replace into userdata (key, userId, data) values (@1, @2, @3)";
                    cmd.AddParam("@1", key);
                    cmd.AddParam("@2", userId);
                    cmd.AddParam("@3", serialized);

                    cmd.Transaction = transaction;

                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                transaction.Commit();
            }
            catch (OperationCanceledException)
            {
                if (transaction != null)
                {
                    transaction.Rollback();
                }

                throw;
            }
            catch (Exception e)
            {
                _logger.ErrorException("Failed to save user data:", e);

                if (transaction != null)
                {
                    transaction.Rollback();
                }

                throw;
            }
            finally
            {
                if (transaction != null)
                {
                    transaction.Dispose();
                }

                _writeLock.Release();
            }
        }

        /// <summary>
        /// Gets the user data.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <param name="key">The key.</param>
        /// <returns>Task{UserItemData}.</returns>
        /// <exception cref="System.ArgumentNullException">
        /// userId
        /// or
        /// key
        /// </exception>
        public UserItemData GetUserData(Guid userId, string key)
        {
            if (userId == Guid.Empty)
            {
                throw new ArgumentNullException("userId");
            }
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentNullException("key");
            }

            return _userData.GetOrAdd(GetInternalKey(userId, key), keyName => RetrieveUserData(userId, key));
        }

        /// <summary>
        /// Retrieves the user data.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <param name="key">The key.</param>
        /// <returns>Task{UserItemData}.</returns>
        private UserItemData RetrieveUserData(Guid userId, string key)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "select data from userdata where key = @key and userId=@userId";

                var idParam = cmd.Parameters.Add("@key", DbType.String);
                idParam.Value = key;

                var userIdParam = cmd.Parameters.Add("@userId", DbType.Guid);
                userIdParam.Value = userId;

                using (var reader = cmd.ExecuteReader(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult | CommandBehavior.SingleRow))
                {
                    if (reader.Read())
                    {
                        using (var stream = reader.GetMemoryStream(0))
                        {
                            return _jsonSerializer.DeserializeFromStream<UserItemData>(stream);
                        }
                    }
                }

                return new UserItemData();
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private readonly object _disposeLock = new object();

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="dispose"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool dispose)
        {
            if (dispose)
            {
                try
                {
                    lock (_disposeLock)
                    {
                        if (_connection != null)
                        {
                            if (_connection.IsOpen())
                            {
                                _connection.Close();
                            }

                            _connection.Dispose();
                            _connection = null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error disposing database", ex);
                }
            }
        }
    }
}