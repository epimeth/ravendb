﻿// -----------------------------------------------------------------------
//  <copyright file="StartupTaskTests.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System;
using System.ComponentModel.Composition.Hosting;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Raven.Abstractions.Data;
using Raven.Abstractions.FileSystem;
using Raven.Bundles.LiveTest;
using Raven.Client.Embedded;
using Raven.Client.FileSystem;
using Raven.Database.Config;
using Raven.Tests.Common;

using Xunit;

namespace Raven.Tests.Bundles.LiveTest
{
	public class StartupTaskTests : RavenTest
	{
		protected override void ModifyConfiguration(InMemoryRavenConfiguration configuration)
		{
			configuration.Catalog.Catalogs.Add(new AssemblyCatalog(typeof(LiveTestDatabaseDocumentPutTrigger).Assembly));
		}

		[Fact]
		public void CleanerStartupTaskShouldRemoveDatabasesAfterIdleTimeout()
		{
			using (var store = NewDocumentStore())
			{
				store
					.DatabaseCommands
					.GlobalAdmin
					.CreateDatabase(new DatabaseDocument
					{
						Id = "Northwind",
						Settings =
						{
							{ "Raven/ActiveBundles", "Replication" },
							{ "Raven/DataDir", NewDataPath() }
						}
					});

				store
					.DatabaseCommands
					.GlobalAdmin
					.CreateDatabase(new DatabaseDocument
					{
						Id = "Northwind2",
						Settings =
						{
							{ "Raven/ActiveBundles", "Replication" },
							{ "Raven/DataDir", NewDataPath() }
						}
					});

				store
					.DatabaseCommands
					.GlobalAdmin
					.CreateDatabase(new DatabaseDocument
					{
						Id = "Northwind3",
						Settings =
						{
							{ "Raven/ActiveBundles", "Replication" },
							{ "Raven/DataDir", NewDataPath() }
						}
					});

				store
					.DatabaseCommands
					.ForDatabase("Northwind2")
					.GetStatistics();

				store
					.DatabaseCommands
					.ForDatabase("Northwind3")
					.GetStatistics();

				Assert.NotNull(store.DatabaseCommands.Get(Constants.RavenDatabasesPrefix + "Northwind"));
				Assert.NotNull(store.DatabaseCommands.Get(Constants.RavenDatabasesPrefix + "Northwind2"));

				store.ServerIfEmbedded
					.ServerStartupTasks.OfType<LiveTestResourceCleanerStartupTask>().First()
					.ExecuteCleanup(null);

				Assert.Null(store.DatabaseCommands.Get(Constants.RavenDatabasesPrefix + "Northwind"));
				Assert.NotNull(store.DatabaseCommands.Get(Constants.RavenDatabasesPrefix + "Northwind2"));

				store.ServerIfEmbedded.Server.Options.DatabaseLandlord.LastRecentlyUsed["Northwind2"] = DateTime.MinValue;

				store.ServerIfEmbedded
					.ServerStartupTasks.OfType<LiveTestResourceCleanerStartupTask>().First()
					.ExecuteCleanup(null);

				Assert.Null(store.DatabaseCommands.Get(Constants.RavenDatabasesPrefix + "Northwind2"));
				Assert.NotNull(store.DatabaseCommands.Get(Constants.RavenDatabasesPrefix + "Northwind3"));
			}
		}

		[Fact]
		public async Task CleanerStartupTaskShouldRemoveFileSystemsAfterIdleTimeout()
		{
			using (var server = GetNewServer())
			using (var store = NewRemoteDocumentStore(ravenDbServer: server))
			using (var fStore = new FilesStore { Url = store.Url, DefaultFileSystem = store.DefaultDatabase }.Initialize())
			{
				await fStore.AsyncFilesCommands.Admin.CreateFileSystemAsync(new FileSystemDocument
				{
					Id = "Northwind",
					Settings =
					{
						{ "Raven/FileSystem/DataDir", NewDataPath() }
					}
				}, "Northwind");

				await fStore.AsyncFilesCommands.Admin.CreateFileSystemAsync(new FileSystemDocument
				{
					Id = "Northwind2",
					Settings =
					{
						{ "Raven/FileSystem/DataDir", NewDataPath() }
					}
				}, "Northwind2");

				await fStore.AsyncFilesCommands.Admin.CreateFileSystemAsync(new FileSystemDocument
				{
					Id = "Northwind3",
					Settings =
					{
						{ "Raven/FileSystem/DataDir", NewDataPath() }
					}
				}, "Northwind3");

				await fStore
					.AsyncFilesCommands
					.ForFileSystem("Northwind2")
					.GetStatisticsAsync();

				await fStore
					.AsyncFilesCommands
					.ForFileSystem("Northwind3")
					.GetStatisticsAsync();

				Assert.NotNull(store.DatabaseCommands.ForSystemDatabase().Get(Constants.RavenFileSystemsPrefix + "Northwind"));
				Assert.NotNull(store.DatabaseCommands.ForSystemDatabase().Get(Constants.RavenFileSystemsPrefix + "Northwind2"));

				server
					.ServerStartupTasks.OfType<LiveTestResourceCleanerStartupTask>().First()
					.ExecuteCleanup(null);

				Assert.Null(store.DatabaseCommands.ForSystemDatabase().Get(Constants.RavenFileSystemsPrefix + "Northwind"));
				Assert.NotNull(store.DatabaseCommands.ForSystemDatabase().Get(Constants.RavenFileSystemsPrefix + "Northwind2"));

				server.Server.Options.FileSystemLandlord.LastRecentlyUsed["Northwind2"] = DateTime.MinValue;

				server
					.ServerStartupTasks.OfType<LiveTestResourceCleanerStartupTask>().First()
					.ExecuteCleanup(null);

				Assert.Null(store.DatabaseCommands.ForSystemDatabase().Get(Constants.RavenFileSystemsPrefix + "Northwind2"));
				Assert.NotNull(store.DatabaseCommands.ForSystemDatabase().Get(Constants.RavenFileSystemsPrefix + "Northwind3"));
			}
		}
	}
}