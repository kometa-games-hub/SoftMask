﻿using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using UnityEngine;

namespace SoftMasking.Tests {
    [ExecuteInEditMode]
    public class AutomatedTest : MonoBehaviour {
        static readonly string TestScenesPath = "Assets/Extra/Test/Scenes/";

        public bool speedUp = false;

        [SerializeField] List<ScreenValidationRuleKeyValuePair> _validationRulePairs = new List<ScreenValidationRuleKeyValuePair>();
        [SerializeField] List<Texture2D> _lastExecutionScreens = new List<Texture2D>();
        [SerializeField] ReferenceScreens _referenceScreens = new ReferenceScreens();
        AutomatedTestResult _result = null;
        bool _updatedAtLeastOnce = false;
        List<ExpectedLogRecord> _expectedLog = new List<ExpectedLogRecord>();
        List<LogRecord> _lastExecutionLog = new List<LogRecord>();
        AutomatedTestError _explicitFail;

        public int referenceStepsCount {
            get { return _referenceScreens.count; }
        }
        public IEnumerable<ScreenValidationRule> validationRules {
            get { return _validationRulePairs.Select(x => x.rule); }
        }
        public bool isReferenceEmpty {
            get { return referenceStepsCount == 0; }
        }
        public int lastExecutionStepsCount {
            get { return _lastExecutionScreens.Count; }
        }
        public bool isLastExecutionEmpty {
            get { return _lastExecutionScreens.Count == 0; }
        }
        public bool isFinished {
            get { return _result != null; }
        }
        public AutomatedTestResult result {
            get { return _result; }
        }

        public event Action<AutomatedTest> changed;

    #if UNITY_EDITOR
        public void SaveLastRecordAsExample() {
            _referenceScreens.ReplaceBy(_lastExecutionScreens);
            NotifyChanged();
        }

        public void DeleteReference() {
            _referenceScreens.Clear();
            _validationRulePairs.Clear();
            NotifyChanged();
        }
    #endif
        
        public void ExpectLog(ExpectedLogRecord expectedRecord) {
            _expectedLog.Add(expectedRecord);
        }

        public void ExpectLog(string messagePattern, LogType logType, UnityEngine.Object context) {
            _expectedLog.Add(new ExpectedLogRecord(messagePattern, logType, context));
        }

        public YieldInstruction Proceed(float delaySeconds = 0f) {
            var saveScreenshotCoroutine = StartCoroutine(ProcessImpl());
            return WaitForDelayAfterStep(delaySeconds, saveScreenshotCoroutine);
        }
        
        IEnumerator ProcessImpl() {
            if (!_updatedAtLeastOnce) { // TODO it would be clearer to refer ResolutionUtility's coroutine here?
                // Seems like 2019.1 needs at least two frames to adjust canvas after a game view size change
                yield return null;
                yield return null;
            }
            yield return new WaitForEndOfFrame();
            if (!isFinished) {
                var texture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0, false);
                _lastExecutionScreens.Add(texture);
                NotifyChanged();
            }
        }

        YieldInstruction WaitForDelayAfterStep(float delaySeconds, Coroutine stepCoroutine) {
            return delaySeconds > 0f && !speedUp
                ? new WaitForSeconds(delaySeconds)
                : (YieldInstruction)stepCoroutine;
        }

        public YieldInstruction ProceedAnimation(Animator animator, float normalizedTime) {
            return StartCoroutine(ProcessAnimation(animator, normalizedTime));
        }
        
        IEnumerator ProcessAnimation(Animator animator, float normalizedTime) {
            if (!_updatedAtLeastOnce)
                yield return null; // to prevent execution before Update
            if (!speedUp)
                while (GetAnimationTime(animator) < normalizedTime)
                    yield return null;
            var state = animator.GetCurrentAnimatorStateInfo(0);
            animator.Play(state.shortNameHash, 0, normalizedTime);            
            yield return StartCoroutine(ProcessImpl());
        }
        
        float GetAnimationTime(Animator animator) {
            return animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
        }

        public IEnumerator Fail(string reason) {
            if (!isFinished) {
                _explicitFail = new AutomatedTestError(reason, lastExecutionStepsCount - 1);
                yield return Finish();
            }
            yield break;
        }

        public YieldInstruction Finish() {
            EjectLogHandler();
            _result = Validate();
            NotifyChanged();
            return new WaitForEndOfFrame();
        }
        
        AutomatedTestResult Validate() {
            var errors = new List<AutomatedTestError>();
            var unexpectedLog = _expectedLog.Aggregate(_lastExecutionLog, (log, pat) => pat.Filter(log));
            if (_explicitFail != null)
                errors.Add(_explicitFail);
            else if (unexpectedLog.Count > 0)
                errors.Add(new AutomatedTestError(
                    string.Format("{0} unexpected log records occured. First unexpected:\n{1}",
                        unexpectedLog.Count,
                        unexpectedLog[0].message)));
            else if (_expectedLog.Count > _lastExecutionLog.Count)
                errors.Add(new AutomatedTestError(
                    string.Format("Not all expected log records occured. Expected: {0}, occured: {1}",
                        _expectedLog.Count,
                        _lastExecutionLog.Count)));
            else if (_lastExecutionScreens.Count != _referenceScreens.count)
                errors.Add(new AutomatedTestError(
                    string.Format("Expected {0} steps but {1} occured.", 
                        _referenceScreens.count,
                        _lastExecutionScreens.Count)));
            else
                for (int step = 0; step < _lastExecutionScreens.Count; ++step) {
                    var validator = ValidationRuleForStep(step);
                    if (!validator.Validate(_referenceScreens[step], _lastExecutionScreens[step])) {
                        File.WriteAllBytes("actual.png", _lastExecutionScreens[step].EncodeToPNG());
                        File.WriteAllBytes("ref.png", _referenceScreens[step].EncodeToPNG());
                        File.WriteAllBytes("diff.png", validator.Diff(_referenceScreens[step], _lastExecutionScreens[step]).EncodeToPNG());
                        errors.Add(new AutomatedTestError(
                            string.Format("Screenshots differ at step {0}.", step), 
                            step, 
                            validator.Diff(_referenceScreens[step], _lastExecutionScreens[step])));
                        break;
                    }
                }
            return new AutomatedTestResult(currentSceneRelativeDir, errors);
        }

        ScreenValidationRule ValidationRuleForStep(int stepIndex) {
            var rulePair = _validationRulePairs.FirstOrDefault(x => x.MatchesIndex(stepIndex));
            return rulePair != null ? rulePair.rule : ScreenValidationRule.topLeftWholeScreen;
        }

        void NotifyChanged() {
            changed.InvokeSafe(this);
        }

        class LogHandler : ILogHandler {
            readonly List<LogRecord> _log;
            readonly ILogHandler _originalHandler;

            public LogHandler(List<LogRecord> log, ILogHandler original) {
                _log = log;
                _originalHandler = original;
            }

            public ILogHandler originalHandler { get { return _originalHandler; } }

            public void LogException(Exception exception, UnityEngine.Object context) {
                _log.Add(new LogRecord(exception.Message, LogType.Exception, context));
                _originalHandler.LogException(exception, context);
            }

            public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args) {
                _log.Add(new LogRecord(string.Format(format, args), logType, context));
                _originalHandler.LogFormat(logType, context, format, args);
            }
        }

        public void Awake() {
        #if UNITY_EDITOR
            _referenceScreens.Load(currentSceneRelativeDir);
        #else
            _referenceScreens.RemoveObsoletes();
        #endif
            if (Application.isPlaying)
                InjectLogHandler();
        }

        public void Start() {
            _lastExecutionScreens.Clear();
            if (Application.isPlaying)
                ResolutionUtility.SetTestResolution();
        }
        
        public void Update() {
            _updatedAtLeastOnce = true;
        }

        public void OnDestroy() {
            if (Application.isPlaying) {
                ResolutionUtility.RevertTestResolution();
                EjectLogHandler();
            }
        }

        public void OnValidate() {
            foreach (var pair in _validationRulePairs)
                pair.rule.RoundRect();
        }

        void InjectLogHandler() {
            Debug.unityLogger.logHandler = new LogHandler(_lastExecutionLog, Debug.unityLogger.logHandler);
        }

        void EjectLogHandler() {
            var injectedHandler = Debug.unityLogger.logHandler as LogHandler;
            if (injectedHandler != null)
                Debug.unityLogger.logHandler = injectedHandler.originalHandler;
        }

        
        string currentSceneRelativeDir {
            get {
                var currentScenePath = gameObject.scene.path;
                return currentScenePath.StartsWith(TestScenesPath)
                    ? currentScenePath.Substring(TestScenesPath.Length).Replace(".unity", "")
                    : gameObject.scene.name;
            }
        }
    }
}
