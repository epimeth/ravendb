﻿// -----------------------------------------------------------------------
//  <copyright file="SqlReplicationHandler.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Server.Documents.ETL.Providers.SQL.RelationalWriters;
using Raven.Server.Json;
using Raven.Server.Routing;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Server.Documents.ETL.Providers.SQL.Handlers
{
    public class SqlReplicationHandler : DatabaseRequestHandler
    {
        [RavenAction("/databases/*/sql-replication/stats", "GET", "/databases/{databaseName:string}/sql-replication/stats?name={sqlReplicationName:string}")]
        public Task GetStats()
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");
            var replication = Database.EtlLoader.SqlTargets.FirstOrDefault(r => r.Name == name);

            if (replication == null)
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return Task.CompletedTask;
            }

            JsonOperationContext context;
            using (ServerStore.ContextPool.AllocateOperationContext(out context))
            {
                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, replication.Statistics.ToBlittable());
                }
            }
            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/sql-replication/debug/stats", "GET", "/databases/{databaseName:string}/sql-replication/debug/stats")]
        public Task GetDebugStats()
        {
            JsonOperationContext context;
            using (ServerStore.ContextPool.AllocateOperationContext(out context))
            {
                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    writer.WriteStartArray();
                    bool first = true;
                    foreach (var replication in Database.EtlLoader.SqlTargets)
                    {
                        if (first == false)
                            writer.WriteComma();
                        else
                            first = false;

                        var json = new DynamicJsonValue
                        {
                            ["Name"] = replication.Name,
                            ["Statistics"] = replication.Statistics.ToBlittable(),
                            ["Metrics"] = replication.Metrics.ToSqlEtlMetricsData(),
                        };
                        context.Write(writer, json);
                    }
                    writer.WriteEndArray();
                }
            }
            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/sql-replication/debug/pref", "GET", "/databases/{databaseName:string}/sql-replication/debug/pref")]
        public Task GetDebugPref()
        {
            /* TODO: Implement this */
            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/sql-replication/test-sql-connection", "GET", "/databases/{databaseName:string}/sql-replication/test-sql-connection?factoryName={factoryName:string}&connectionString{connectionString:string}")]
        public Task GetTestSqlConnection()
        {
            try
            {
                var factoryName = GetStringQueryString("factoryName");
                var connectionString = GetStringQueryString("connectionString");
                RelationalDatabaseWriter.TestConnection(factoryName, connectionString);
                NoContentStatus();
            }
            catch (Exception ex)
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest; // Bad Request

                if (Logger.IsInfoEnabled)
                    Logger.Info("Error occured during sql replication connection test", ex);

                JsonOperationContext context;
                using (ContextPool.AllocateOperationContext(out context))
                {
                    using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                    {
                        context.Write(writer, new DynamicJsonValue
                        {
                            ["Error"] = "Connection failed",
                            ["Exception"] = ex.ToString(),
                        });
                    }
                }
            }

            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/sql-replication/simulate", "POST", "/databases/{databaseName:string}/sql-replication/simulate")]
        public Task PostSimulateSqlReplication()
        {
            DocumentsOperationContext context;
            using (Database.DocumentsStorage.ContextPool.AllocateOperationContext(out context))
            {
                context.OpenReadTransaction();

                var dbDoc = context.ReadForMemory(RequestBodyStream(), "SimulateSqlReplicationResult");
                var simulateSqlReplication = JsonDeserializationServer.SimulateSqlReplication(dbDoc);
                var result = SqlEtl.SimulateSqlEtl(simulateSqlReplication, Database, context);

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, result);
                }
            }

            return Task.CompletedTask;
        }
    }
}