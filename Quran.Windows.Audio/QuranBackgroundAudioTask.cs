﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Quran.Core;
using Quran.Core.Common;
using Quran.Core.Utils;
using Windows.ApplicationModel.Background;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Quran.Windows.Audio
{
    public sealed class QuranBackgroundAudioTask : IBackgroundTask
    {
        private const string TitleKey = "title";
        private const string QuranTrackKey = "quranTrack";
        private const string ReciterKey = "reciter";
        private const string SurahKey = "surah";
        private const string AyahKey = "ayah";
        private const string AlbumArtKey = "albumart";
        const int RETRY_COUNT = 5;
        const uint RPC_S_SERVER_UNAVAILABLE = 0x800706BA;
        const uint E_ABORT = 0x80004004;
        const uint E_INVALID_STATE = 0xC00D36B2;

        private SystemMediaTransportControls smtc;
        private MediaPlaybackList _playbackList;
        private QuranAudioTrack _originalTrackRequest;
        private ManualResetEvent _backgroundTaskStarted = new ManualResetEvent(false);
        private BackgroundTaskDeferral _deferral; // Used to keep task alive
        private TelemetryClient telemetry = new TelemetryClient();
        private int repeatCount = 0;
        private int currentAyahRepeatCount = 0;
        public void Run(IBackgroundTaskInstance taskInstance)
        {
            Debug.WriteLine("Background Audio Task " + taskInstance.Task.Name + " starting...");

            // Initialize SystemMediaTransportControls (SMTC) for integration with
            // the Universal Volume Control (UVC).
            //
            // The UI for the UVC must update even when the foreground process has been terminated
            // and therefore the SMTC is configured and updated from the background task.
            smtc = BackgroundMediaPlayer.Current.SystemMediaTransportControls;
            smtc.ButtonPressed += AudioControlButtonPressed;
            smtc.PropertyChanged += AudioControlPropertyChanged;
            smtc.IsEnabled = true;
            smtc.IsPauseEnabled = true;
            smtc.IsPlayEnabled = true;
            smtc.IsNextEnabled = true;
            smtc.IsPreviousEnabled = true;

            // Add handlers for MediaPlayer
            BackgroundMediaPlayer.Current.CurrentStateChanged += MediaPlayerStateChanged;
            BackgroundMediaPlayer.Current.MediaEnded += MediaPlayerMediaEnded;

            // Initialize message channel 
            BackgroundMediaPlayer.MessageReceivedFromForeground += MessageReceivedFromForeground;

            // Send information to foreground that background task has been started if app is active
            MessageService.SendMessageToForeground(new BackgroundAudioTaskStartedMessage());

            _deferral = taskInstance.GetDeferral(); // This must be retrieved prior to subscribing to events below which use it

            // Mark the background task as started to unblock SMTC Play operation (see related WaitOne on this signal)
            _backgroundTaskStarted.Set();

            // Associate a cancellation and completed handlers with the background task.
            taskInstance.Task.Completed += TaskCompleted;
            taskInstance.Canceled += new BackgroundTaskCanceledEventHandler(OnCanceled); // event may raise immediately before continung thread excecution so must be at the end
        }

        /// <summary>
        /// Fires when any SystemMediaTransportControl property is changed by system or user
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        void AudioControlPropertyChanged(SystemMediaTransportControls sender, SystemMediaTransportControlsPropertyChangedEventArgs args)
        {
            // TODO: If soundlevel turns to muted, app can choose to pause the music
        }

        /// <summary>
        /// This function controls the button events from UVC.
        /// This code if not run in background process, will not be able to handle button pressed events when app is suspended.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void AudioControlButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    Debug.WriteLine("UVC play button pressed");

                    // When the background task has been suspended and the SMTC
                    // starts it again asynchronously, some time is needed to let
                    // the task startup process in Run() complete.

                    // Wait for task to start. 
                    // Once started, this stays signaled until shutdown so it won't wait
                    // again unless it needs to.
                    bool result = _backgroundTaskStarted.WaitOne(5000);
                    if (!result)
                    {
                        throw new Exception("Background Task didnt initialize in time");
                    }

                    StartPlayback();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    Debug.WriteLine("UVC pause button pressed");
                    try
                    {
                        BackgroundMediaPlayer.Current.Pause();
                    }
                    catch (Exception ex)
                    {
                        telemetry.TrackException(ex, new Dictionary<string, string> { { "Scenario", "BackgroundAudioButtonPressed" } });
                    }
                    break;
                case SystemMediaTransportControlsButton.Next:
                    Debug.WriteLine("UVC next button pressed");
                    SkipToNext();
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    Debug.WriteLine("UVC previous button pressed");
                    SkipToPrevious();
                    break;
            }
        }

        private void MediaPlayerStateChanged(MediaPlayer sender, object args)
        {
            try
            {
                if (sender.CurrentState == MediaPlayerState.Playing)
                {
                    smtc.PlaybackStatus = MediaPlaybackStatus.Playing;
                }
                else if (sender.CurrentState == MediaPlayerState.Paused)
                {
                    smtc.PlaybackStatus = MediaPlaybackStatus.Paused;
                }
                else if (sender.CurrentState == MediaPlayerState.Closed)
                {
                    smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                }
            }
            catch (Exception ex)
            {
                if ((uint)ex.HResult == E_ABORT)
                {
                    telemetry.TrackException(ex, new Dictionary<string, string> { { "Scenario", "BackgroundAudioStateChanged" } });
                }
                else
                {
                    throw;
                }
            }
        }

        /// <summary>
        /// Raised when a message is recieved from the foreground app
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        async void MessageReceivedFromForeground(object sender, MediaPlayerDataReceivedEventArgs e)
        {
            StartPlaybackMessage startPlaybackMessage;
            if (MessageService.TryParseMessage(e.Data, out startPlaybackMessage))
            {
                //Foreground App process has signalled that it is ready for playback
                Debug.WriteLine("Starting Playback");
                StartPlayback();
                return;
            }

            SkipNextMessage skipNextMessage;
            if (MessageService.TryParseMessage(e.Data, out skipNextMessage))
            {
                // User has chosen to skip track from app context.
                Debug.WriteLine("Skipping to next");
                SkipToNext();
                return;
            }

            SkipPreviousMessage skipPreviousMessage;
            if (MessageService.TryParseMessage(e.Data, out skipPreviousMessage))
            {
                // User has chosen to skip track from app context.
                Debug.WriteLine("Skipping to previous");
                SkipToPrevious();
                return;
            }

            TrackChangedMessage trackChangedMessage;
            if (MessageService.TryParseMessage(e.Data, out trackChangedMessage))
            {
                if (trackChangedMessage.AudioTrack != null)
                {
                    await ChangeTrack(trackChangedMessage.AudioTrack);
                    return;
                }
            }

            RepeatTrackMessage repeatTrackMessage;
            if (MessageService.TryParseMessage(e.Data, out repeatTrackMessage))
            {
                // User has chosen to skip track from app context.
                repeatCount = repeatTrackMessage.RepeatCount;
                Debug.WriteLine(String.Format("Repeat track request, RepeatCount ({0})", repeatCount));

                if (repeatCount == 0)
                {
                    BackgroundMediaPlayer.Current.IsLoopingEnabled = false;
                }
                else if(currentAyahRepeatCount < repeatCount || repeatCount==-1)
                {
                    BackgroundMediaPlayer.Current.IsLoopingEnabled = true;
                }

                return;
            }

        }

        private async Task ChangeTrack(QuranAudioTrack newTrack)
        {
            _originalTrackRequest = newTrack;
            int index = FindTrack(newTrack);

            // If playlist contains track - change track
            if (index >= 0)
            {
                await ChangeTrack((uint)index);
            }
            // Otherwise load new playlist and then change track
            else
            {
                await CreatePlaybackList(newTrack);
                await ChangeTrack(newTrack);
            }
        }

        private async Task ChangeTrack(uint index)
        {
            if (_playbackList.CurrentItemIndex != index)
            {
                smtc.PlaybackStatus = MediaPlaybackStatus.Changing;
                for (int i = 0; i < RETRY_COUNT; i++)
                {
                    try
                    {
                        _playbackList.MoveTo(index);
                        break;
                    }
                    catch (Exception e)
                    {
                        if ((uint)e.HResult == E_INVALID_STATE)
                        {
                            await Task.Delay(1000);
                        }
                    }
                }
                //TODO: Work around playlist bug that doesn't continue playing after a switch; remove later
            }
            BackgroundMediaPlayer.Current.Play();
        }

        /// <summary>
        /// Gets track index from playbackList if found. Otherwise -1.
        /// </summary>
        /// <param name="newTrack"></param>
        /// <returns></returns>
        private int FindTrack(QuranAudioTrack newTrack)
        {
            if (_playbackList != null)
            {
                return _playbackList.Items.ToList().FindIndex(i =>
                    (int)i.Source.CustomProperties[AyahKey] == newTrack.Ayah &&
                    (int)i.Source.CustomProperties[SurahKey] == newTrack.Surah &&
                    (int)i.Source.CustomProperties[ReciterKey] == newTrack.ReciterId);
            }

            return -1;
        }

        /// <summary>
        /// Create a playback list from the list of songs received from the foreground app.
        /// </summary>
        /// <param name="songs"></param>
        private async Task CreatePlaybackList(QuranAudioTrack newTrack)
        {
            BackgroundMediaPlayer.Current.Pause();

            // Make a new list
            if (_playbackList != null)
            {
                _playbackList.CurrentItemChanged -= PlaybackListCurrentItemChanged;
                _playbackList.Items.Clear();
            }
            else
            {
                _playbackList = new MediaPlaybackList();
            }

            // Initialize FileUtils
            await FileUtils.Initialize(newTrack.ScreenInfo);

            var missingFiles = await AudioUtils.GetMissingFiles(newTrack);

            // Add playback items to the list
            QuranAudioTrack nextTrack = newTrack.GetFirstAyah();
            var reciter = nextTrack.GetReciter();
            while (nextTrack != null)
            {
                string fileName = AudioUtils.GetFileName(nextTrack.GetQuranAyah(), reciter);
                MediaSource source;
                if (missingFiles.Contains(fileName))
                {
                    source = MediaSource.CreateFromUri(new Uri(Path.Combine(reciter.ServerUrl, fileName)));
                }
                else
                {
                    var file = await FileUtils.GetFile(await reciter.GetStorageFolder(), fileName);
                    if (file != null)
                    {
                        source = MediaSource.CreateFromStorageFile(file);
                        //source = MediaSource.CreateFromUri(new Uri(file.Path));
                    }
                    else
                    {
                        source = MediaSource.CreateFromUri(new Uri(Path.Combine(reciter.ServerUrl, fileName)));
                    }
                }
                source.CustomProperties[SurahKey] = nextTrack.Surah;
                source.CustomProperties[AyahKey] = nextTrack.Ayah;
                source.CustomProperties[ReciterKey] = nextTrack.ReciterId;
                source.CustomProperties[QuranTrackKey] = nextTrack.ToString();
                source.CustomProperties[TitleKey] = QuranUtils.GetSurahAyahString(nextTrack.Surah, nextTrack.Ayah);
                _playbackList.Items.Add(new MediaPlaybackItem(source));
                nextTrack = nextTrack.GetNextInSurah();
            }

            // Don't auto start
            BackgroundMediaPlayer.Current.AutoPlay = false;

            // Assign the list to the player
            BackgroundMediaPlayer.Current.Source = _playbackList;

            // Add handler for future playlist item changes
            _playbackList.CurrentItemChanged += PlaybackListCurrentItemChanged;
        }

        /// <summary>
        /// Start playlist and change UVC state
        /// </summary>
        private void StartPlayback()
        {
            try
            {
                BackgroundMediaPlayer.Current.Play();
            }
            catch (Exception ex)
            {
                telemetry.TrackException(ex, new Dictionary<string, string> { { "Scenario", "BackgroundAudioPlay" } });
                Debug.WriteLine(ex.ToString());
            }
        }

        /// <summary>
        /// Event fired when track is ended
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        async void MediaPlayerMediaEnded(MediaPlayer sender, object args)
        {
            if (_originalTrackRequest != null && 
                _playbackList.CurrentItemIndex == _playbackList.Items.Count - 1)
            {
                await ChangeTrack(_originalTrackRequest.GetLastAyah().GetNext());
            }
            MessageService.SendMessageToForeground(new TrackEndedMessage());
        }

        /// <summary>
        /// Skip track and update UVC via SMTC
        /// </summary>
        async void SkipToPrevious()
        {
            smtc.PlaybackStatus = MediaPlaybackStatus.Changing;
            currentAyahRepeatCount = 0;

            if (_playbackList.CurrentItemIndex == 0)
            {
                if (_originalTrackRequest != null)
                {
                    await ChangeTrack(_originalTrackRequest.GetFirstAyah().GetPrevious());
                }
            }
            else
            {
                _playbackList.MovePrevious();
            }
        }

        /// <summary>
        /// Skip track and update UVC via SMTC
        /// </summary>
        void SkipToNext()
        {
            smtc.PlaybackStatus = MediaPlaybackStatus.Changing;
            currentAyahRepeatCount = 0;
            if (_playbackList.CurrentItemIndex == _playbackList.Items.Count - 1)
            {
                MediaPlayerMediaEnded(BackgroundMediaPlayer.Current, null);
            }
            else
            {
                _playbackList.MoveNext();
            }
        }

        /// <summary>
        /// Raised when playlist changes to a new track
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        void PlaybackListCurrentItemChanged(MediaPlaybackList sender, CurrentMediaPlaybackItemChangedEventArgs args)
        {

            if (repeatCount > 0 )
            {
                // Get the new item
                if (args.OldItem == null)
                {
                    currentAyahRepeatCount = 0;
                    Debug.WriteLine("First ayah in the list, CurrentAyahRepeatCount({0}), PrevAyah(NA), NextAyah({1})", currentAyahRepeatCount, (int)args.NewItem.Source.CustomProperties[AyahKey]);
                    //per current implementation bismillah ayah will always be first in the list
                    if (QuranUtils.IsBismillah(QuranAyah.FromString(string.Format("{0}:{1}", args.NewItem.Source.CustomProperties[SurahKey], args.NewItem.Source.CustomProperties[AyahKey]))))
                    {
                        currentAyahRepeatCount = repeatCount;
                        Debug.WriteLine("It is a Bismillah ayah, CurrentAyahRepeatCount({0}), PrevAyah(NA), NextAyah({1})", currentAyahRepeatCount, (int)args.NewItem.Source.CustomProperties[AyahKey]);
                    }
                }
                //ayah just repeated
                else if (args.NewItem != null && (int)args.NewItem.Source.CustomProperties[AyahKey] == (int)args.OldItem.Source.CustomProperties[AyahKey])
                {
                    currentAyahRepeatCount = currentAyahRepeatCount + 1;
                    Debug.WriteLine("First ayah in the list, CurrentAyahRepeatCount({0}), PrevAyah({1}), NextAyah({2})", currentAyahRepeatCount, (int)args.OldItem.Source.CustomProperties[AyahKey], (int)args.NewItem.Source.CustomProperties[AyahKey]);
                }
                //ayah just changed
                else if (args.NewItem != null && (int)args.NewItem.Source.CustomProperties[AyahKey] != (int)args.OldItem.Source.CustomProperties[AyahKey])
                {
                    currentAyahRepeatCount = 0;
                    Debug.WriteLine("Next ayah in the list, CurrentAyahRepeatCount({0}), PrevAyah({2}), NextAyah({1})", currentAyahRepeatCount, (int)args.NewItem.Source.CustomProperties[AyahKey], (int)args.OldItem.Source.CustomProperties[AyahKey]);
                }

                if (currentAyahRepeatCount < repeatCount)
                {
                    BackgroundMediaPlayer.Current.IsLoopingEnabled = true;
                    Debug.WriteLine(String.Format("Ayah Repeat Condition True, Current Count({0}),Expected Count ({1})", currentAyahRepeatCount, repeatCount));
                }
                else
                {
                    BackgroundMediaPlayer.Current.IsLoopingEnabled = false;
                    Debug.WriteLine(String.Format("Ayah Repeat Condition False, Current Count({0}),Expected Count ({1})", currentAyahRepeatCount, repeatCount));
                }
            }

            // Update the system view
            UpdateUVCOnNewTrack(args.NewItem);

            // Get the current track
            if (args.NewItem != null && args.NewItem.Source.CustomProperties.ContainsKey(QuranTrackKey))
            {
                var json = args.NewItem.Source.CustomProperties[QuranTrackKey] as string;
                MessageService.SendMessageToForeground(new TrackChangedMessage(QuranAudioTrack.FromString(json)));
            }      
        }

        /// <summary>
        /// Update Universal Volume Control (UVC) using SystemMediaTransPortControl APIs
        /// </summary>
        private void UpdateUVCOnNewTrack(MediaPlaybackItem item)
        {
            if (item == null)
            {
                smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
                smtc.DisplayUpdater.MusicProperties.Title = string.Empty;
                smtc.DisplayUpdater.Update();
                return;
            }

            smtc.PlaybackStatus = MediaPlaybackStatus.Playing;
            smtc.DisplayUpdater.Type = MediaPlaybackType.Music;
            smtc.DisplayUpdater.MusicProperties.Title = item.Source.CustomProperties[TitleKey] as string;

            // TODO: Add image
            //var albumArtUri = item.Source.CustomProperties[AlbumArtKey] as Uri;
            //if (albumArtUri != null)
            //    smtc.DisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromUri(albumArtUri);
            //else
            //    smtc.DisplayUpdater.Thumbnail = null;

            smtc.DisplayUpdater.Update();
        }

        /// <summary>
        /// Indicate that the background task is completed.
        /// </summary>       
        async void TaskCompleted(BackgroundTaskRegistration sender, BackgroundTaskCompletedEventArgs args)
        {
            Debug.WriteLine("MyBackgroundAudioTask " + sender.TaskId + " Completed...");
            _deferral.Complete();            
        }

        /// <summary>
        /// Handles background task cancellation. Task cancellation happens due to:
        /// 1. Another Media app comes into foreground and starts playing music 
        /// 2. Resource pressure. Your task is consuming more CPU and memory than allowed.
        /// In either case, save state so that if foreground app resumes it can know where to start.
        /// </summary>
        private void OnCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
        {
            // You get some time here to save your state before process and resources are reclaimed
            Debug.WriteLine("MyBackgroundAudioTask " + sender.Task.TaskId + " Cancel Requested...");
            currentAyahRepeatCount = 0;
            repeatCount = 0;
            try
            {
                // immediately set not running
                _backgroundTaskStarted.Reset();

                // unsubscribe from list changes
                if (_playbackList != null)
                {
                    _playbackList.CurrentItemChanged -= PlaybackListCurrentItemChanged;
                    _playbackList = null;
                }

                // remove handlers for MediaPlayer
                BackgroundMediaPlayer.Current.CurrentStateChanged -= MediaPlayerStateChanged;
                BackgroundMediaPlayer.Current.MediaEnded -= MediaPlayerMediaEnded;

                // unsubscribe event handlers
                BackgroundMediaPlayer.MessageReceivedFromForeground -= MessageReceivedFromForeground;
                smtc.ButtonPressed -= AudioControlButtonPressed;
                smtc.PropertyChanged -= AudioControlPropertyChanged;

                BackgroundMediaPlayer.Shutdown(); // shutdown media pipeline
            }
            catch (Exception ex)
            {
                telemetry.TrackException(ex, new Dictionary<string, string> { { "Scenario", "BackgroundAudioCancel" } });
            }
            _deferral.Complete(); // signals task completion. 
            Debug.WriteLine("MyBackgroundAudioTask Cancel complete...");
        }
    }
}
