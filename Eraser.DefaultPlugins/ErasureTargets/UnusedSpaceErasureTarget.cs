﻿/* 
 * $Id$
 * Copyright 2008-2010 The Eraser Project
 * Original Author: Joel Low <lowjoel@users.sourceforge.net>
 * Modified By:
 * 
 * This file is part of Eraser.
 * 
 * Eraser is free software: you can redistribute it and/or modify it under the
 * terms of the GNU General Public License as published by the Free Software
 * Foundation, either version 3 of the License, or (at your option) any later
 * version.
 * 
 * Eraser is distributed in the hope that it will be useful, but WITHOUT ANY
 * WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR
 * A PARTICULAR PURPOSE. See the GNU General Public License for more details.
 * 
 * A copy of the GNU General Public License can be found at
 * <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using System.IO;

using Eraser.Manager;
using Eraser.Util;
using Eraser.Util.ExtensionMethods;

namespace Eraser.DefaultPlugins
{
	/// <summary>
	/// Class representing a unused space erase.
	/// </summary>
	[Serializable]
	[Guid("A627BEC4-CAFC-46ce-92AD-209157C3177A")]
	public class UnusedSpaceErasureTarget : ErasureTarget
	{
		#region Serialization code
		protected UnusedSpaceErasureTarget(SerializationInfo info, StreamingContext context)
			: base(info, context)
		{
			Drive = (string)info.GetValue("Drive", typeof(string));
			EraseClusterTips = (bool)info.GetValue("EraseClusterTips", typeof(bool));
		}

		[SecurityPermission(SecurityAction.Demand, SerializationFormatter = true)]
		public override void GetObjectData(SerializationInfo info, StreamingContext context)
		{
			base.GetObjectData(info, context);
			info.AddValue("Drive", Drive);
			info.AddValue("EraseClusterTips", EraseClusterTips);
		}
		#endregion

		/// <summary>
		/// Constructor.
		/// </summary>
		public UnusedSpaceErasureTarget()
			: base()
		{
		}

		public override Guid Guid
		{
			get { return GetType().GUID; }
		}

		public override string Name
		{
			get { return S._("Unused disk space"); }
		}

		public sealed override ErasureMethod EffectiveMethod
		{
			get
			{
				if (Method != ErasureMethodRegistrar.Default)
					return base.EffectiveMethod;

				return ManagerLibrary.Instance.ErasureMethodRegistrar[
					ManagerLibrary.Settings.DefaultUnusedSpaceErasureMethod];
			}
		}

		public override bool SupportsMethod(ErasureMethod method)
		{
			return method == ErasureMethodRegistrar.Default ||
				method is UnusedSpaceErasureMethod;
		}

		/// <summary>
		/// Override the base class property so that we won't need to keep casting
		/// </summary>
		protected new SteppedProgressManager Progress
		{
			get
			{
				return (SteppedProgressManager)base.Progress;
			}
			set
			{
				base.Progress = value;
			}
		}

		public override string UIText
		{
			get { return S._("Unused disk space ({0})", Drive); }
		}

		public override IErasureTargetConfigurer Configurer
		{
			get { return new UnusedSpaceErasureTargetConfigurer(); }
		}

		/// <summary>
		/// The drive to erase
		/// </summary>
		public string Drive { get; set; }

		/// <summary>
		/// Whether cluster tips should be erased.
		/// </summary>
		public bool EraseClusterTips { get; set; }

		public override void Execute()
		{
			//Check for sufficient privileges to run the unused space erasure.
			if (!Security.IsAdministrator())
			{
				if (Environment.OSVersion.Platform == PlatformID.Win32NT &&
					Environment.OSVersion.Version >= new Version(6, 0))
				{
					Logger.Log(S._("The program does not have the required permissions to erase " +
						"the unused space on disk. Run the program as an administrator and retry " +
						"the operation."), LogLevel.Error);
				}
				else
				{
					Logger.Log(S._("The program does not have the required permissions to erase " +
						"the unused space on disk."), LogLevel.Error);
				}

				return;
			}

			//Check whether System Restore has any available checkpoints.
			if (SystemRestore.GetInstances().Count != 0)
			{
				Logger.Log(S._("This computer has had System Restore or Volume Shadow Copies " +
					"enabled. This may allow copies of files stored on the disk to be recovered " +
					"and pose a security concern.", Drive), LogLevel.Warning);
			}

			//If the user is under disk quotas, log a warning message
			if (VolumeInfo.FromMountPoint(Drive).HasQuota)
				Logger.Log(S._("The drive {0} has disk quotas active. This will prevent the " +
					"complete erasure of unused space and may pose a security concern.",
					Drive), LogLevel.Warning);

			//Get the erasure method if the user specified he wants the default.
			ErasureMethod method = EffectiveMethod;

			//Make a folder to dump our temporary files in
			DirectoryInfo info = new DirectoryInfo(Drive);
			VolumeInfo volInfo = VolumeInfo.FromMountPoint(Drive);
			FileSystem fsManager = ManagerLibrary.Instance.FileSystemRegistrar[volInfo];

			//Start sampling the speed of the task.
			Progress = new SteppedProgressManager();
			Task.Progress.Steps.Add(new SteppedProgressManagerStep(
				Progress, 1.0f / Task.Targets.Count));

			//Erase the cluster tips of every file on the drive.
			if (EraseClusterTips)
			{
				//Define the callback handlers
				ProgressManager tipSearch = new ProgressManager();
				Progress.Steps.Add(new SteppedProgressManagerStep(tipSearch,
					0.0f, S._("Searching for files' cluster tips...")));
				tipSearch.Total = 1;
				ClusterTipsSearchProgress searchProgress = delegate(string path)
				{
					if (Task.Canceled)
						throw new OperationCanceledException(S._("The task was cancelled."));

					OnProgressChanged(this, new ProgressChangedEventArgs(tipSearch,
						new TaskProgressChangedEventArgs(path, 0, 0)));
				};

				ProgressManager tipProgress = new ProgressManager();
				Progress.Steps.Add(new SteppedProgressManagerStep(tipProgress, 0.1f,
					S._("Erasing cluster tips...")));
				ClusterTipsEraseProgress eraseProgress =
					delegate(int currentFile, int totalFiles, string currentFilePath)
					{
						tipSearch.MarkComplete();
						tipProgress.Total = totalFiles;
						tipProgress.Completed = currentFile;
						OnProgressChanged(this, new ProgressChangedEventArgs(tipProgress,
							new TaskProgressChangedEventArgs(currentFilePath, 0, 0)));

						if (Task.Canceled)
							throw new OperationCanceledException(S._("The task was cancelled."));
					};

				//Start counting statistics
				fsManager.EraseClusterTips(VolumeInfo.FromMountPoint(Drive),
					method, searchProgress, eraseProgress);
				tipProgress.MarkComplete();
			}

			bool lowDiskSpaceNotifications = Shell.LowDiskSpaceNotificationsEnabled;
			info = info.CreateSubdirectory(Path.GetFileName(
				FileSystem.GenerateRandomFileName(info, 18)));
			try
			{
				//Set the folder's compression flag off since we want to use as much
				//space as possible
				if (info.IsCompressed())
					info.Uncompress();

				//Disable the low disk space notifications
				Shell.LowDiskSpaceNotificationsEnabled = false;

				//Fill the disk
				EraseUnusedSpace(volInfo, info, fsManager, method);

				//Erase old resident file system table files
				ProgressManager residentProgress = new ProgressManager();
				Progress.Steps.Add(new SteppedProgressManagerStep(residentProgress,
					0.05f, S._("Old resident file system table files")));
				fsManager.EraseOldFileSystemResidentFiles(volInfo, info, method,
					delegate(int currentFile, int totalFiles)
					{
						residentProgress.Completed = currentFile;
						residentProgress.Total = totalFiles;
						OnProgressChanged(this, new ProgressChangedEventArgs(residentProgress,
							new TaskProgressChangedEventArgs(string.Empty, 0, 0)));

						if (Task.Canceled)
							throw new OperationCanceledException(S._("The task was cancelled."));
					}
				);

				residentProgress.MarkComplete();
			}
			finally
			{
				//Remove the folder holding all our temporary files.
				ProgressManager tempFiles = new ProgressManager();
				Progress.Steps.Add(new SteppedProgressManagerStep(tempFiles,
					0.0f, S._("Removing temporary files...")));
				OnProgressChanged(this, new ProgressChangedEventArgs(tempFiles,
					new TaskProgressChangedEventArgs(string.Empty, 0, 0)));
				info.Delete(true);
				tempFiles.Completed = tempFiles.Total;

				//Reset the low disk space notifications
				Shell.LowDiskSpaceNotificationsEnabled = lowDiskSpaceNotifications;
			}

			//Then clean the old file system entries
			ProgressManager structureProgress = new ProgressManager();
			Progress.Steps.Add(new SteppedProgressManagerStep(structureProgress,
				0.05f, S._("Erasing unused directory structures...")));
			fsManager.EraseDirectoryStructures(volInfo,
				delegate(int currentFile, int totalFiles)
				{
					if (Task.Canceled)
						throw new OperationCanceledException(S._("The task was cancelled."));

					//Compute the progress
					structureProgress.Total = totalFiles;
					structureProgress.Completed = currentFile;

					//Set the event parameters, then broadcast the progress event.
					OnProgressChanged(this, new ProgressChangedEventArgs(structureProgress,
						new TaskProgressChangedEventArgs(string.Empty, 0, 0)));
				}
			);

			structureProgress.MarkComplete();
			Progress = null;
		}

		private void EraseUnusedSpace(VolumeInfo volInfo, DirectoryInfo info, FileSystem fsInfo,
			ErasureMethod method)
		{
			ProgressManager mainProgress = new ProgressManager();
			Progress.Steps.Add(new SteppedProgressManagerStep(mainProgress,
				EraseClusterTips ? 0.8f : 0.9f, S._("Erasing unused space...")));

			//Continue creating files while there is free space.
			while (volInfo.AvailableFreeSpace > 0)
			{
				//Generate a non-existant file name
				string currFile = FileSystem.GenerateRandomFileName(info, 18);

				//Create the stream
				FileStream stream = new FileStream(currFile, FileMode.CreateNew,
					FileAccess.Write, FileShare.None, 8, FileOptions.WriteThrough);
				try
				{
					//Set the length of the file to be the amount of free space left
					//or the maximum size of one of these dumps.
					mainProgress.Total = mainProgress.Completed +
						method.CalculateEraseDataSize(null, volInfo.AvailableFreeSpace);
					long streamLength = Math.Min(ErasureMethod.FreeSpaceFileUnit,
						volInfo.AvailableFreeSpace);

					//Handle IO exceptions gracefully, because the filesystem
					//may require more space than demanded by us for file allocation.
					while (true)
						try
						{
							stream.SetLength(streamLength);
							break;
						}
						catch (IOException)
						{
							if (streamLength > volInfo.ClusterSize)
								streamLength -= volInfo.ClusterSize;
							else
								throw;
						}

					//Then run the erase task
					method.Erase(stream, long.MaxValue,
						ManagerLibrary.Instance.PrngRegistrar[ManagerLibrary.Settings.ActivePrng],
						delegate(long lastWritten, long totalData, int currentPass)
						{
							mainProgress.Completed += lastWritten;
							OnProgressChanged(this, new ProgressChangedEventArgs(mainProgress,
								new TaskProgressChangedEventArgs(Drive, currentPass, method.Passes)));

							if (Task.Canceled)
								throw new OperationCanceledException(S._("The task was cancelled."));
						}
					);
				}
				finally
				{
					stream.Close();
					fsInfo.ResetFileTimes(new FileInfo(currFile));
				}
			}

			//Mark the main bulk of the progress as complete
			mainProgress.MarkComplete();
		}
	}
}
