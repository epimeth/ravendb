﻿using System.Collections.Generic;
using System.ComponentModel.Composition.Primitives;
using System.Threading;
using Raven.Abstractions;
using Raven.Abstractions.Connection;
using Raven.Abstractions.Counters;
using Raven.Abstractions.Data;
using Raven.Abstractions.Exceptions;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Smuggler;
using Raven.Abstractions.Smuggler.Data;
using Raven.Client.Counters;
using Raven.Client.Linq;
using Raven.Database.Counters;
using Raven.Database.Server;
using Raven.Imports.Newtonsoft.Json;
using Raven.Json.Linq;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace Raven.Database.Smuggler
{
	public class SmugglerCounterApi : ISmugglerApi<CounterConnectionStringOptions, SmugglerCounterOptions, CounterOperationState>
	{
		private readonly ICounterStore counterStore;
		private const string IncrementalExportStateFile = "IncrementalExport.state.json";
		private const string CounterIncrementalDump = ".counter-incremental-dump";

		private readonly CancellationToken cancellationToken;

		public SmugglerCounterApi(ICounterStore counterStore, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (counterStore == null) throw new ArgumentNullException("counterStore");
			this.counterStore = counterStore;
			this.cancellationToken = cancellationToken;
			Options = new SmugglerCounterOptions();
		}

		public SmugglerCounterOptions Options { get; private set; }

		/// <summary>
		/// Export counter data to specified destination (a file or a stream)
		/// </summary>
		/// <param name="exportOptions">options to specify the source and destination of the export</param>
		/// <exception cref="UnauthorizedAccessException">The caller does not have the required permission.-or- specified a file that is read-only. </exception>
		/// <exception cref="DirectoryNotFoundException">The specified path is invalid (for example, it is on an unmapped drive). </exception>
		/// <exception cref="IOException">An I/O error occurred while creating the file. </exception>
		/// <exception cref="SmugglerExportException">Encapsulates exception that happens when actually exporting data. See InnerException for details.</exception>
		public async Task<CounterOperationState> ExportData(SmugglerExportOptions<CounterConnectionStringOptions> exportOptions)
		{
			var result = new CounterOperationState();
			var exportFolder = String.Empty;
			if (Options.Incremental)
			{
				if (Directory.Exists(exportOptions.ToFile) == false)
				{
					if (File.Exists(exportOptions.ToFile))
						exportOptions.ToFile = Path.GetDirectoryName(exportOptions.ToFile) ?? exportOptions.ToFile;
					else
						Directory.CreateDirectory(exportOptions.ToFile);
				}
				exportFolder = exportOptions.ToFile;

				exportOptions.ToFile = Path.Combine(exportOptions.ToFile, SystemTime.UtcNow.ToString("yyyy-MM-dd-HH-mm-0", CultureInfo.InvariantCulture) + CounterIncrementalDump);
				if (File.Exists(exportOptions.ToFile))
				{
					var counter = 1;
					while (true)
					{
						exportOptions.ToFile = Path.Combine(Path.GetDirectoryName(exportOptions.ToFile), SystemTime.UtcNow.ToString("yyyy-MM-dd-HH-mm", CultureInfo.InvariantCulture) + "-" + counter + CounterIncrementalDump);

						if (File.Exists(exportOptions.ToFile) == false)
							break;
						counter++;
					}
				}
			}

			SmugglerExportException lastException = null;

			var ownedStream = exportOptions.ToStream == null;
			var stream = exportOptions.ToStream ?? File.Create(exportOptions.ToFile);

			try
			{
				using (var gZipStream = new GZipStream(stream, CompressionMode.Compress, leaveOpen: true))
				using (var streamWriter = new StreamWriter(gZipStream))
				{
					var jsonWriter = new JsonTextWriter(streamWriter)
					{
						Formatting = Formatting.Indented
					};
					jsonWriter.WriteStartObject();			
					jsonWriter.WritePropertyName(Options.Incremental ? "CountersDeltas" : "CounterSnapshots"); //also for human readability
					jsonWriter.WriteStartArray();

					try
					{
						if (Options.Incremental)
							await ExportIncrementalData(exportFolder, jsonWriter).WithCancellation(cancellationToken).ConfigureAwait(false);
						else
							await ExportFullData(jsonWriter).WithCancellation(cancellationToken).ConfigureAwait(false);
					}
					catch (SmugglerExportException e)
					{
						Debug.Assert(e.Data.Keys.Cast<string>().Contains("LastEtag"));
						result.LastWrittenEtag = (long)e.Data["LastEtag"];
						lastException = e;
					}

					jsonWriter.WriteEndArray();
					jsonWriter.WriteEndObject();
					streamWriter.Flush();
				}

				if (lastException != null)
					throw lastException;
				return result;
			}
			finally
			{
				if (ownedStream && stream != null)
					stream.Dispose();
			}
		}

		private async Task ExportIncrementalData(string exportFilename, JsonTextWriter jsonWriter)
		{
			var lastEtag = ReadLastEtagFromStateFile(exportFilename);
			var counterDeltas = (await GetCounterStatesSinceEtag(lastEtag).WithCancellation(cancellationToken).ConfigureAwait(false)).ToList();

			foreach (var delta in counterDeltas)
			{
				jsonWriter.WriteStartObject();
				jsonWriter.WritePropertyName("CounterName");
				jsonWriter.WriteValue(delta.CounterName);

				jsonWriter.WritePropertyName("GroupName");
				jsonWriter.WriteValue(delta.GroupName);

				jsonWriter.WritePropertyName("Sign");
				jsonWriter.WriteValue(delta.Sign);

				jsonWriter.WritePropertyName("Value");
				jsonWriter.WriteValue(delta.Value);
				jsonWriter.WriteEndObject();
			}

			if (counterDeltas.Count > 0)
			{
				var etag = counterDeltas.Max(x => x.Etag);
				WriteLastEtagToStateFile(exportFilename, etag);
			}
		}

		private async Task<IEnumerable<CounterState>> GetCounterStatesSinceEtag(long etag)
		{
			var deltas = new List<CounterState>();
			do
			{
				var deltasFromRequest = await counterStore.Advanced.GetCounterStatesSinceEtag(etag, token: cancellationToken).ConfigureAwait(false);
				if (deltasFromRequest.Count == 0)
					break;

				etag = deltasFromRequest.Max(x => x.Etag);
				deltas.AddRange(deltasFromRequest);
			} while (true);

			return deltas;
		}

		private static long ReadLastEtagFromStateFile(string exportFilename)
		{
			var exportStateFilePath = Path.Combine(exportFilename, IncrementalExportStateFile);
			if (File.Exists(exportStateFilePath) == false)
				return 0;

			using (var streamReader = new StreamReader(File.Open(exportStateFilePath,FileMode.OpenOrCreate)))
			{
				var jsonObject = RavenJToken.ReadFrom(new JsonTextReader(streamReader));
				long lastWrittenEtag;
				var lastWrittenEtagString = jsonObject.Value<string>("LastWrittenEtag");
				if(Int64.TryParse(lastWrittenEtagString,out lastWrittenEtag) == false)
					throw new InvalidDataException("Failed to parse incremental export status file. Found in file : " + lastWrittenEtagString);

				return lastWrittenEtag;
			}
		}

		private static void WriteLastEtagToStateFile(string exportFilename, long lastEtag)
		{
			var exportStateFile = Path.Combine(exportFilename,IncrementalExportStateFile);
			using (var streamWriter = new StreamWriter(File.Open(exportStateFile, FileMode.Create)))
			{
				new RavenJObject
					{
						{ "LastWrittenEtag", lastEtag.ToString(CultureInfo.InvariantCulture) },
					}.WriteTo(new JsonTextWriter(streamWriter));
				streamWriter.Flush();
			}
		}

		private async Task ExportFullData(JsonTextWriter jsonWriter)
		{
			var counterStorageNames = await counterStore.Admin.GetCounterStoragesNamesAsync(cancellationToken).ConfigureAwait(false);
			foreach (var storageName in counterStorageNames)
			{
				var counterStorageInfo = await counterStore.Admin.GetCounterStorageSummary(storageName, cancellationToken).ConfigureAwait(false);
					
				jsonWriter.WriteStartArray();
					foreach (var counterInfo in counterStorageInfo)
					{
						jsonWriter.WriteStartObject();
						jsonWriter.WritePropertyName("Group");
						jsonWriter.WriteValue(counterInfo.Group);

						jsonWriter.WritePropertyName("Name");
						jsonWriter.WriteValue(counterInfo.CounterName);
						jsonWriter.WritePropertyName("Positive");
						jsonWriter.WriteValue(counterInfo.Increments);

						jsonWriter.WritePropertyName("Negative");
						jsonWriter.WriteValue(counterInfo.Decrements);

						jsonWriter.WriteEndObject();
					}
				jsonWriter.WriteEndArray();
			}
		}

		//assumes that the caller has responsibility to handle data stream disposal ("stream" parameter)
		private async Task ImportFullData(CounterConnectionStringOptions connectionString, Stream stream)
		{
			CountingStream sizeStream;
			JsonTextReader jsonReader;
			if (SmugglerHelper.TryGetJsonReaderForStream(stream, out jsonReader, out sizeStream) == false)
			{
				throw new InvalidOperationException("Failed to get reader for the data stream.");
			}

			if(jsonReader.TokenType != JsonToken.StartObject)
				throw new InvalidDataException("StartObject was expected");

			ICounterStore store = null;
			try
			{
				if (jsonReader.Read() == false && jsonReader.TokenType != JsonToken.StartArray)
					throw new InvalidDataException("StartArray was expected");

				store = new CounterStore
				{
					Url = connectionString.Url,
					Name = connectionString.CounterStoreId,
					Credentials = new OperationCredentials(connectionString.ApiKey, connectionString.Credentials)
				};
				store.Initialize();
				
				var existingCounterGroupsAndNames = await store.Admin.GetCounterStorageNameAndGroups(connectionString.CounterStoreId, cancellationToken);

				while (jsonReader.Read() && jsonReader.TokenType != JsonToken.EndArray)
				{
					if (jsonReader.TokenType == JsonToken.StartObject)
					{
						var counterInfo = (RavenJObject) RavenJToken.ReadFrom(jsonReader);

						var delta = Math.Abs(counterInfo.Value<long>("Positive")) - Math.Abs(counterInfo.Value<long>("Negative"));
						var groupName = counterInfo.Value<string>("Group");
						var counterName = counterInfo.Value<string>("Name");

						if (existingCounterGroupsAndNames.Any(x => x.Group == groupName && x.Name == counterName))
							await store.ResetAsync(groupName, counterName, cancellationToken).ConfigureAwait(false); //since it is a full import, the values are overwritten

						store.Batch.ScheduleChange(groupName, counterName, delta);
					}
				}

				await store.Batch.FlushAsync().ConfigureAwait(false);
			}
			finally
			{
				if(store != null)
					store.Dispose();				
			}
		}

		
		/// <summary>
		/// Import counter data from a dump file
		/// </summary>
		/// <param name="importOptions">options that specify the source and destination of the data</param>
		/// <exception cref="ArgumentException">FromXXXX, To, Url and CounterStoreId parameters must be present in the import options</exception>
		public async Task ImportData(SmugglerImportOptions<CounterConnectionStringOptions> importOptions)
		{
			if (String.IsNullOrWhiteSpace(importOptions.FromFile) && importOptions.FromStream == null)
				throw new ArgumentException("Missing from paramter from import options - be sure to define either FromFile or FromStream property");

			if(importOptions.To == null)
				throw new ArgumentException("Missing To parameter from importOptions - do not know where to import to.");

			if (String.IsNullOrWhiteSpace(importOptions.To.Url))
				throw new ArgumentException("Missing Url of the RavenDB server - do not know where to import to");

			if(String.IsNullOrWhiteSpace(importOptions.To.CounterStoreId))
				throw new ArgumentException("Missing Id of the Counter Store - do not know where to import to");

			if (Options.Incremental == false)
			{
				var stream = importOptions.FromStream;
				var ownStream = false;
				try
				{
					if (stream == null)
					{
						stream = File.OpenRead(importOptions.FromFile);
						ownStream = true;
					}

					await ImportFullData(importOptions.To, stream).WithCancellation(cancellationToken).ConfigureAwait(false);
				}
				finally
				{
					if (stream != null && ownStream)
						stream.Dispose();
				}
			}
			else
			{
				var files = Directory.GetFiles(Path.GetFullPath(importOptions.FromFile))
					.Where(file => CounterIncrementalDump.Equals(Path.GetExtension(file), StringComparison.CurrentCultureIgnoreCase))
					.OrderBy(File.GetLastWriteTimeUtc)
					.ToArray();

				if (files.Length == 0)
					return;

				foreach(var file in files)
				{
					using (var fileStream = File.OpenRead(Path.Combine(importOptions.FromFile, file)))
					{
						//Operations.ShowProgress("Starting to import file: {0}", files[i]);
						await ImportIncrementalData(importOptions.To, fileStream).WithCancellation(cancellationToken).ConfigureAwait(false);
					}
				}
			}
		}

		private async Task ImportIncrementalData(CounterConnectionStringOptions connectionString, Stream stream)
		{
			CountingStream sizeStream;
			JsonTextReader jsonReader;
			if (SmugglerHelper.TryGetJsonReaderForStream(stream, out jsonReader, out sizeStream) == false)
			{
				throw new InvalidOperationException("Failed to get reader for the data stream.");
			}

			if (jsonReader.TokenType != JsonToken.StartObject)
				throw new InvalidDataException("StartObject was expected");

			ICounterStore store = null;

			try
			{
				if (jsonReader.Read() == false && jsonReader.TokenType != JsonToken.StartArray)
					throw new InvalidDataException("StartArray was expected");

				store = new CounterStore
				{
					Url = connectionString.Url,
					Name = connectionString.CounterStoreId,
					Credentials = new OperationCredentials(connectionString.ApiKey, connectionString.Credentials)
				};
				store.Initialize();

				while (jsonReader.Read() && jsonReader.TokenType != JsonToken.EndArray)
				{
					if (jsonReader.TokenType == JsonToken.StartObject)
					{
						var counterDelta = RavenJToken.ReadFrom(jsonReader).ToObject<CounterState>();
						store.Batch.ScheduleChange(counterDelta.GroupName, counterDelta.CounterName, counterDelta.Value);
					}
				}

				await store.Batch.FlushAsync().WithCancellation(cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				if (store != null)
					store.Dispose();
			}
		}

		public async Task Between(SmugglerBetweenOptions<CounterConnectionStringOptions> betweenOptions)
		{
			if (betweenOptions.ReportProgress == null)
				betweenOptions.ReportProgress = msg => { };
			using (var source = new CounterStore
			{
				Url = betweenOptions.From.Url,
				Name = betweenOptions.From.CounterStoreId,
				Credentials = new OperationCredentials(betweenOptions.From.ApiKey, betweenOptions.From.Credentials)
			})
			using (var target = new CounterStore
			{
				Url = betweenOptions.To.Url,
				Name = betweenOptions.To.CounterStoreId,
				Credentials = new OperationCredentials(betweenOptions.To.ApiKey, betweenOptions.To.Credentials)
			})
			{
				source.Initialize();
				target.Initialize();
				var existingCounterGroupsAndNames = await target.Admin.GetCounterStorageNameAndGroups(betweenOptions.To.CounterStoreId, cancellationToken);
				var counterSummaries = await source.Admin.GetCounterStorageSummary(betweenOptions.From.CounterStoreId, cancellationToken);
				foreach (var summary in counterSummaries)
				{
					if (existingCounterGroupsAndNames.Any(x => x.Group == summary.Group && x.Name == summary.CounterName))
						await target.ResetAsync(summary.Group, summary.CounterName, cancellationToken)
									.WithCancellation(cancellationToken)
									.ConfigureAwait(false); //since it is a full import, the values are overwritten
					target.Batch.ScheduleChange(summary.Group, summary.CounterName, summary.Total);
				}

				await target.Batch.FlushAsync().WithCancellation(cancellationToken).ConfigureAwait(false);
			}
		}
	}
}