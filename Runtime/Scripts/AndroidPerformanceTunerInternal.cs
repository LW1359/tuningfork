//-----------------------------------------------------------------------
// <copyright file="AndroidPerformanceTunerInternal.cs" company="Google">
//
// Copyright 2020 Google Inc. All Rights Reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections;
using AOT;
using Google.Protobuf;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Google.Android.PerformanceTuner
{
    /// <summary>
    ///     Internal part
    /// </summary>
    public partial class AndroidPerformanceTuner<TFidelity, TAnnotation>
        where TFidelity : class, IMessage<TFidelity>, new()
        where TAnnotation : class, IMessage<TAnnotation>, new()
    {
        // Use only for marshalling static delegates.
        static AndroidPerformanceTuner<TFidelity, TAnnotation> s_ActiveTf;
        readonly AdditionalLibraryMethods<TFidelity, TAnnotation> m_AdditionalLibraryMethods;
        readonly FidelityParamsCallback m_FpCallback = FidelityParamsCallbackImpl;
        readonly ILibraryMethods m_Library;
        readonly UploadCallback m_UploadCallback = UploadCallbackImpl;
        Action m_OnStop;
        FrameTracer m_SceneObject;
        SetupConfig m_SetupConfig;
        string m_endPoint = null;
        const string k_LocalEndPoint = "http://localhost:9000";

        public AndroidPerformanceTuner()
        {
            s_ActiveTf = this;
            m_Library =
#if UNITY_ANDROID && !UNITY_EDITOR
                new AndroidLibraryMethods();
#else
                new DefaultLibraryMethods();
#endif
            m_AdditionalLibraryMethods = new AdditionalLibraryMethods<TFidelity, TAnnotation>(m_Library);
        }

        ErrorCode StartInternal()
        {
            m_SetupConfig = Resources.Load("SetupConfig") as SetupConfig;

            if (m_SetupConfig == null)
            {
                Debug.LogWarning(
                    "SetupConfig can not be loaded, open Google->Android Performance Tuner to setup the plugin.");
                return ErrorCode.NoSettings;
            }

            if (!m_SetupConfig.pluginEnabled)
            {
                Debug.LogWarning(
                    "Android Performance Tuner plugin is not enabled, open Google->Android Performance Tuner to enable the plugin.");
                return ErrorCode.TuningforkNotInitialized;
            }

            var validAnnotation = CheckAnnotationMessage(m_SetupConfig);
            var validFidelity = CheckFidelityMessage(m_SetupConfig);

            if (validAnnotation != ErrorCode.Ok || validFidelity != ErrorCode.Ok)
                return ErrorCode.BadParameter;


            IMessage defaultQualityParameters = null;
            if (!m_SetupConfig.useAdvancedFidelityParameters)
            {
                defaultQualityParameters = new TFidelity();
                MessageUtil.SetQualityLevel(defaultQualityParameters, QualitySettings.GetQualityLevel());
            }

            var errorCode = m_AdditionalLibraryMethods.Init(m_FpCallback, defaultQualityParameters, m_endPoint);

            if (errorCode != ErrorCode.Ok)
            {
                m_AdditionalLibraryMethods.FreePointers();
                return errorCode;
            }

            m_OnStop += () => m_AdditionalLibraryMethods.FreePointers();

            CreateSceneObject();
            m_SceneObject.StartCoroutine(CallbacksCheck());

            if (!SwappyIsEnabled()) EnableUnityFrameTicks();
            if (!m_SetupConfig.useAdvancedAnnotations) EnableDefaultAnnotationsMode();
            if (!m_SetupConfig.useAdvancedFidelityParameters) EnableDefaultFidelityMode();

            AddUploadCallback();

            return errorCode;
        }

        ErrorCode CheckAnnotationMessage(SetupConfig config)
        {
            if (config.useAdvancedAnnotations) return ErrorCode.Ok;
            if (!MessageUtil.HasLoadingState<TAnnotation>())
            {
                Debug.LogError("Android Performance Tuner is using default annotation, " +
                               "but Annotation message doesn't contain loading state parameter.");
                return ErrorCode.BadParameter;
            }

            if (!MessageUtil.HasScene<TAnnotation>())
            {
                Debug.LogError("Android Performance Tuner is using default annotation, " +
                               "but Annotation message doesn't contain scene parameter.");
                return ErrorCode.BadParameter;
            }

            return ErrorCode.Ok;
        }

        ErrorCode CheckFidelityMessage(SetupConfig config)
        {
            if (config.useAdvancedFidelityParameters) return ErrorCode.Ok;

            if (!MessageUtil.HasQualityLevel<TFidelity>())
            {
                Debug.LogError("Android Performance Tuner is using default fidelity, " +
                               "but Fidelity message doesn't contain level parameter.");
                return ErrorCode.BadParameter;
            }

            return ErrorCode.Ok;
        }

        void EnableDefaultAnnotationsMode()
        {
            SceneManager.activeSceneChanged += OnSceneChanged;
            OnSceneChanged(SceneManager.GetActiveScene(), SceneManager.GetActiveScene());
            m_OnStop += () => { SceneManager.activeSceneChanged -= OnSceneChanged; };
        }

        void AddUploadCallback()
        {
            var errorCode = m_Library.SetUploadCallback(m_UploadCallback);
            if (errorCode != ErrorCode.Ok)
                Debug.LogWarningFormat("Android Performance Tuner: Could not set upload callback, status {0}",
                    errorCode);
        }

        void EnableDefaultFidelityMode()
        {
            onReceiveFidelityParameters += UpdateQualityLevel;
            m_OnStop += () => { onReceiveFidelityParameters -= UpdateQualityLevel; };

            m_SceneObject.StartCoroutine(QualitySettingsCheck());
        }

        void EnableUnityFrameTicks()
        {
            m_SceneObject.StartCoroutine(UnityFrameTick());
        }

        void CreateSceneObject()
        {
            if (m_SceneObject != null) return;
            GameObject gameObject = new GameObject("Android Performance Tuner");
            m_SceneObject = gameObject.AddComponent<FrameTracer>();
            GameObject.DontDestroyOnLoad(gameObject);
            m_OnStop += () =>
            {
                if (m_SceneObject != null)
                {
                    m_SceneObject.StopAllCoroutines();
                    GameObject.Destroy(m_SceneObject.gameObject);
                    m_SceneObject = null;
                }
            };
        }

        void UpdateQualityLevel(TFidelity message)
        {
            if (message == null) return;
            var qualityLevel = MessageUtil.GetQualityLevel(message);
            QualitySettings.SetQualityLevel(qualityLevel);
        }

        void OnSceneChanged(UnityEngine.SceneManagement.Scene from, UnityEngine.SceneManagement.Scene to)
        {
            var annotation = new TAnnotation();
            MessageUtil.SetScene(annotation, to.buildIndex);
            MessageUtil.SetLoadingState(annotation, MessageUtil.LoadingState.NotLoading);
            SetCurrentAnnotation(annotation);
        }

        /// <summary>
        ///     Used if swappy is not available.
        /// </summary>
        IEnumerator UnityFrameTick()
        {
            while (true)
            {
                yield return new WaitForEndOfFrame();
                FrameTick(InstrumentationKeys.UnityFrame);
            }
        }

        IEnumerator QualitySettingsCheck()
        {
            int currentLevel = QualitySettings.GetQualityLevel();
            while (true)
            {
                yield return new WaitForEndOfFrame();
                if (currentLevel != QualitySettings.GetQualityLevel())
                {
                    currentLevel = QualitySettings.GetQualityLevel();
                    TFidelity message = new TFidelity();
                    MessageUtil.SetQualityLevel(message, QualitySettings.GetQualityLevel());
                    m_AdditionalLibraryMethods.SetFidelityParameters(message);
                }
            }
        }

        TFidelity m_ReceivedFidelityParameters = null;
        UploadTelemetryRequest m_UploadTelemetryRequest = null;

        /// <summary>
        ///     Check if new received fidelity parameters or upload telemetry request were stored, 
        ///     and call their callbacks. The C# callbacks are not called directly from the native
        ///     TuningFork callbacks to avoid crashes due to running in a different thread 
        ///     than what Unity is expecting.
        /// </summary>
        IEnumerator CallbacksCheck()
        {
            while (true)
            {
                yield return new WaitForFixedUpdate();
                if (m_ReceivedFidelityParameters != null)
                {
                    if (onReceiveFidelityParameters != null) onReceiveFidelityParameters(m_ReceivedFidelityParameters);
                    m_ReceivedFidelityParameters = null;
                }

                if (m_UploadTelemetryRequest != null)
                {
                    if (onReceiveUploadLog != null) onReceiveUploadLog(m_UploadTelemetryRequest);
                    m_UploadTelemetryRequest = null;
                }
            }
        }

        // These callbacks must be static as il2cpp can not marshall non-static delegates.
        [MonoPInvokeCallback(typeof(UploadCallback))]
        static void UploadCallbackImpl(IntPtr bytes, uint size)
        {
            if (s_ActiveTf == null || s_ActiveTf.onReceiveUploadLog == null) return;
            // Don't call OnReceiveUploadLog directly from this thread.
            s_ActiveTf.m_UploadTelemetryRequest = UploadTelemetryRequest.Parse(bytes, size);
        }

        [MonoPInvokeCallback(typeof(FidelityParamsCallback))]
        static void FidelityParamsCallbackImpl(ref CProtobufSerialization ps)
        {
            if (s_ActiveTf == null && s_ActiveTf.onReceiveUploadLog == null) return;
            // Don't call OnReceiveFidelityParameters directly from this thread.
            s_ActiveTf.m_ReceivedFidelityParameters = ps.ParseMessage<TFidelity>();
        }
    }
}