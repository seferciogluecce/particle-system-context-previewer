using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

internal sealed class ParticleSystemContextPreviewerWindow : EditorWindow
{
    private const string MenuPath = "Tools/Particle System Context Previewer";
    private const float MinimumDeltaTime = 0.0001f;
    private const float MaximumDeltaTime = 0.1f;
    private sealed class ParticlePlaybackState
    {
        public ParticleSystem System;
        public ParticleSystem.Particle[] Particles;
        public bool WasPlaying;
        public bool WasPaused;
    }

    private sealed class SceneViewVisibilityState
    {
        public SceneView SceneView;
        public bool FxEnabled;
        public bool ShowParticleSystems;
    }

    private readonly struct ContextScanResult
    {
        public ContextScanResult(
            string contextKey,
            string contextName,
            List<ParticleSystem> eligibleSystems,
            int skippedCount)
        {
            ContextKey = contextKey;
            ContextName = contextName;
            EligibleSystems = eligibleSystems;
            SkippedCount = skippedCount;
            EligibleSetKey = BuildEligibleSetKey(eligibleSystems);
        }

        public readonly string ContextKey;
        public readonly string ContextName;
        public readonly List<ParticleSystem> EligibleSystems;
        public readonly int SkippedCount;
        public readonly string EligibleSetKey;
    }


    private readonly List<ParticleSystem> particleSystems = new List<ParticleSystem>();
    private readonly List<SceneViewVisibilityState> sceneViewVisibilityStates =
        new List<SceneViewVisibilityState>();
    private readonly Dictionary<ParticleSystem, ParticlePlaybackState> playbackStates =
        new Dictionary<ParticleSystem, ParticlePlaybackState>();

    private Vector2 scrollPosition;
    private bool isPreviewing;
    private bool isPaused;
    private bool pauseSelectionReady;
    private bool pendingPauseSelectionArm;
    private string pauseSelectionEligibleSetKey = string.Empty;
    private bool pendingContextEvaluation;
    private float playbackSpeed = 1f;
    private double lastUpdateTime;
    private int skippedCount;
    private string contextKey = string.Empty;
    private string eligibleSetKey = string.Empty;
    private string contextName = "No context";
    private string statusMessage = "No eligible Particle Systems found in the current context.";
    private MessageType statusType = MessageType.Info;

    [MenuItem(MenuPath, false, 246)]
    private static void Open()
    {
        ParticleSystemContextPreviewerWindow window =
            GetWindow<ParticleSystemContextPreviewerWindow>("Particle System Context Previewer");
        window.minSize = new Vector2(420f, 260f);
        window.Show();
    }

    private void OnEnable()
    {
        EditorApplication.update += TickPreview;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        EditorApplication.hierarchyChanged += OnHierarchyChanged;
        EditorSceneManager.sceneOpened += OnSceneOpened;
        EditorSceneManager.sceneClosed += OnSceneClosed;
        EditorSceneManager.newSceneCreated += OnNewSceneCreated;
        EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChangedInEditMode;
        PrefabStage.prefabStageOpened += OnPrefabStageChanged;
        PrefabStage.prefabStageClosing += OnPrefabStageChanged;

        lastUpdateTime = EditorApplication.timeSinceStartup;
        RefreshContext(false);
    }

    private void OnDisable()
    {
        EditorApplication.update -= TickPreview;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.hierarchyChanged -= OnHierarchyChanged;
        EditorSceneManager.sceneOpened -= OnSceneOpened;
        EditorSceneManager.sceneClosed -= OnSceneClosed;
        EditorSceneManager.newSceneCreated -= OnNewSceneCreated;
        EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChangedInEditMode;
        PrefabStage.prefabStageOpened -= OnPrefabStageChanged;
        PrefabStage.prefabStageClosing -= OnPrefabStageChanged;
        EditorApplication.delayCall -= EvaluatePendingContextChange;
        pendingContextEvaluation = false;

        EndPreviewSession();
    }

    private void OnGUI()
    {
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        EditorGUILayout.LabelField("Particle System Context Previewer", EditorStyles.boldLabel);
        EditorGUILayout.LabelField(
            "Preview eligible Particle Systems in the current Scene or Prefab context without running runtime scripts.",
            EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.Space(4f);

        playbackSpeed = EditorGUILayout.Slider(
            new GUIContent("Playback Speed", "Multiplies Scene/Edit Mode particle preview speed from 0.1x to 4x without running gameplay scripts."),
            playbackSpeed,
            0.1f,
            4f);

        EditorGUILayout.Space(4f);
        DrawPrimaryControls();
        DrawSelectionControl();

        EditorGUILayout.Space(8f);
        DrawStatus();

        EditorGUILayout.EndScrollView();
    }

    private void DrawPrimaryControls()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(!isPreviewing && particleSystems.Count == 0))
            {
                if (GUILayout.Button(isPreviewing ? "Restart All" : "Preview All", GUILayout.Height(28f)))
                {
                    StartOrRestartPreview();
                }
            }

            bool canPause = CanPausePreview();
            using (new EditorGUI.DisabledScope(!canPause))
            {
                string pauseLabel = isPaused ? "Resume" : "Pause";
                GUIContent pauseContent = new GUIContent(
                    pauseLabel,
                    "Use Select Eligible Systems first to pause preview reliably through Unity selection.");
                if (GUILayout.Button(pauseContent, GUILayout.Height(28f)))
                {
                    TogglePause();
                }
            }

            using (new EditorGUI.DisabledScope(!isPreviewing))
            {
                if (GUILayout.Button("Stop Preview", GUILayout.Height(28f)))
                {
                    StopPreview();
                }
            }
        }
    }

    private void DrawSelectionControl()
    {
        using (new EditorGUI.DisabledScope(particleSystems.Count == 0))
        {
            if (GUILayout.Button("Select Eligible Systems"))
            {
                SelectEligibleSystems();
            }
        }
    }

    private void DrawStatus()
    {
        int liveParticleCount = 0;
        for (int i = 0; i < particleSystems.Count; i++)
        {
            ParticleSystem particleSystem = particleSystems[i];
            if (particleSystem != null)
            {
                liveParticleCount += particleSystem.particleCount;
            }
        }

        EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Eligible systems", particleSystems.Count.ToString());
        EditorGUILayout.LabelField("Live particles", liveParticleCount.ToString());
        EditorGUILayout.LabelField("State", GetPreviewStateLabel());
    }

    private string GetPreviewStateLabel()
    {
        if (isPaused)
        {
            return "Paused";
        }

        return isPreviewing ? "Previewing" : "Stopped";
    }

    private void StartOrRestartPreview()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            SetStatus("Edit-mode particle preview is unavailable while entering or running Play Mode.", MessageType.Info);
            Repaint();
            return;
        }

        bool wasPreviewingOrPaused = isPreviewing || isPaused;
        bool restorePauseSelectionGate = !wasPreviewingOrPaused && pauseSelectionReady;
        string restorePauseSelectionEligibleSetKey = pauseSelectionEligibleSetKey;
        bool restoreFailed = RestoreParticleSystems();
        RestoreSceneViewVisibility();
        ResetPauseSelectionGate();
        RefreshContext(true);
        if (particleSystems.Count == 0)
        {
            SetIdleStatus();
            Repaint();
            return;
        }

        CapturePlaybackStates();
        ApplySceneViewVisibility();
        for (int i = particleSystems.Count - 1; i >= 0; i--)
        {
            ParticleSystem particleSystem = particleSystems[i];
            if (particleSystem == null)
            {
                particleSystems.RemoveAt(i);
                continue;
            }

            try
            {
                particleSystem.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                particleSystem.Simulate(0f, false, true, false);
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning(
                    $"Could not preview Particle System '{particleSystem.name}': {exception.Message}",
                    particleSystem);
                particleSystems.RemoveAt(i);
            }
        }

        isPreviewing = particleSystems.Count > 0;
        isPaused = false;
        lastUpdateTime = EditorApplication.timeSinceStartup;

        if (isPreviewing)
        {
            RestorePauseSelectionGateIfStillValid(
                restorePauseSelectionGate,
                restorePauseSelectionEligibleSetKey);
            SetStatus(
                restoreFailed
                    ? $"Previewing {particleSystems.Count} Particle System(s). Previous preview state could not be fully restored; see Console for details."
                    : $"Previewing {particleSystems.Count} Particle System(s).",
                MessageType.Info);
        }
        else
        {
            SetStatus("No Particle Systems could be previewed.", MessageType.Info);
            RestoreSceneViewVisibility();
        }

        UpdateSignaturesFromCurrentSystems();
        SceneView.RepaintAll();
        Repaint();
    }

    private void RestorePauseSelectionGateIfStillValid(bool shouldRestore, string previousEligibleSetKey)
    {
        if (!shouldRestore ||
            previousEligibleSetKey != eligibleSetKey ||
            particleSystems.Count == 0 ||
            !IsCurrentSelectionEligibleSystems())
        {
            ResetPauseSelectionGate();
            return;
        }

        pauseSelectionReady = true;
        pendingPauseSelectionArm = false;
        pauseSelectionEligibleSetKey = previousEligibleSetKey;
    }

    private void TogglePause()
    {
        isPaused = !isPaused;
        lastUpdateTime = EditorApplication.timeSinceStartup;
        SetStatus(isPaused ? "Preview paused." : "Preview resumed.", MessageType.Info);
        SceneView.RepaintAll();
        Repaint();
    }

    private void StopPreview()
    {
        bool wasPreviewing = isPreviewing || isPaused;
        bool restoreFailed = EndPreviewSession();
        lastUpdateTime = EditorApplication.timeSinceStartup;
        RefreshContext(true);

        if (restoreFailed)
        {
            SetRestoreFailureStatus();
        }
        else if (wasPreviewing)
        {
            SetStatus("Preview stopped. Original Particle System states were restored where possible.", MessageType.Info);
        }
        else
        {
            SetIdleStatus();
        }

        SceneView.RepaintAll();
        Repaint();
    }

    private void TickPreview()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            lastUpdateTime = EditorApplication.timeSinceStartup;
            return;
        }

        if (!isPreviewing)
        {
            lastUpdateTime = EditorApplication.timeSinceStartup;
            return;
        }

        EnforceSceneViewVisibility();

        if (isPaused)
        {
            if (!CanPausePreview())
            {
                isPaused = false;
                SetStatus("Preview resumed because the eligible Particle System selection changed.", MessageType.Info);
                Repaint();
            }

            lastUpdateTime = EditorApplication.timeSinceStartup;
            return;
        }

        double now = EditorApplication.timeSinceStartup;
        float deltaTime = Mathf.Clamp(
            (float)(now - lastUpdateTime) * playbackSpeed,
            MinimumDeltaTime,
            MaximumDeltaTime);
        lastUpdateTime = now;

        for (int i = particleSystems.Count - 1; i >= 0; i--)
        {
            ParticleSystem particleSystem = particleSystems[i];
            if (!IsEligible(particleSystem))
            {
                StopPreviewBecauseAvailableSystemsChanged();
                return;
            }

            try
            {
                particleSystem.Simulate(deltaTime, false, false, false);
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning(
                    $"Stopped previewing Particle System '{particleSystem.name}': {exception.Message}",
                    particleSystem);
                StopPreviewBecauseAvailableSystemsChanged();
                return;
            }
        }

        SceneView.RepaintAll();
        Repaint();
    }

    private void RefreshContext(bool preserveStatus)
    {
        ContextScanResult scan = ScanContext();
        ApplyScan(scan);

        if (!preserveStatus)
        {
            SetIdleStatus();
        }

        Repaint();
    }

    private ContextScanResult ScanContext()
    {
        List<ParticleSystem> eligibleSystems = new List<ParticleSystem>();
        int skipped = 0;
        string nextContextName;
        string nextContextKey;

        PrefabStage prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
        if (prefabStage != null && prefabStage.prefabContentsRoot != null)
        {
            GameObject root = prefabStage.prefabContentsRoot;
            nextContextName = $"Prefab Stage: {root.name}";
            nextContextKey = "prefab:" + root.GetInstanceID();
            AddFromRoot(root, eligibleSystems, ref skipped);
        }
        else
        {
            int loadedSceneCount = 0;
            List<int> sceneHandles = new List<int>();
            for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                Scene scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.isLoaded)
                {
                    continue;
                }

                loadedSceneCount++;
                sceneHandles.Add(scene.handle);
                GameObject[] roots = scene.GetRootGameObjects();
                for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
                {
                    AddFromRoot(roots[rootIndex], eligibleSystems, ref skipped);
                }
            }

            sceneHandles.Sort();
            nextContextKey = "scenes:" + string.Join(",", sceneHandles.ConvertAll(handle => handle.ToString()).ToArray());
            nextContextName = loadedSceneCount == 1
                ? $"Scene: {SceneManager.GetActiveScene().name}"
                : $"Loaded scenes: {loadedSceneCount}";
        }

        return new ContextScanResult(nextContextKey, nextContextName, eligibleSystems, skipped);
    }

    private void ApplyScan(ContextScanResult scan)
    {
        particleSystems.Clear();
        particleSystems.AddRange(scan.EligibleSystems);
        skippedCount = scan.SkippedCount;
        contextName = scan.ContextName;
        contextKey = scan.ContextKey;
        eligibleSetKey = scan.EligibleSetKey;

        if (pauseSelectionReady &&
            (pauseSelectionEligibleSetKey != eligibleSetKey || !IsCurrentSelectionEligibleSystems()))
        {
            ResetPauseSelectionGate();
        }
    }

    private void CapturePlaybackStates()
    {
        for (int i = 0; i < particleSystems.Count; i++)
        {
            ParticleSystem particleSystem = particleSystems[i];
            if (particleSystem == null || playbackStates.ContainsKey(particleSystem))
            {
                continue;
            }

            try
            {
                ParticleSystem.Particle[] particles =
                    new ParticleSystem.Particle[particleSystem.particleCount];
                int particleCount = particleSystem.GetParticles(particles);
                if (particleCount != particles.Length)
                {
                    System.Array.Resize(ref particles, particleCount);
                }

                playbackStates.Add(particleSystem, new ParticlePlaybackState
                {
                    System = particleSystem,
                    Particles = particles,
                    WasPlaying = particleSystem.isPlaying,
                    WasPaused = particleSystem.isPaused
                });
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning(
                    $"Could not preserve Particle System '{particleSystem.name}' before preview: {exception.Message}",
                    particleSystem);
            }
        }
    }

    private bool RestoreParticleSystems()
    {
        bool restoreFailed = false;
        foreach (ParticlePlaybackState state in playbackStates.Values)
        {
            ParticleSystem particleSystem = state.System;
            if (particleSystem == null)
            {
                continue;
            }

            try
            {
                particleSystem.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);

                if (state.WasPlaying)
                {
                    particleSystem.Play(false);
                }
                else if (state.WasPaused)
                {
                    particleSystem.Play(false);
                    particleSystem.Pause(false);
                }
                else
                {
                    particleSystem.Stop(false, ParticleSystemStopBehavior.StopEmitting);
                }

                particleSystem.SetParticles(state.Particles, state.Particles.Length);
            }
            catch (System.Exception exception)
            {
                restoreFailed = true;
                Debug.LogWarning(
                    $"Could not restore Particle System '{particleSystem.name}' after preview: {exception.Message}",
                    particleSystem);
            }
        }

        playbackStates.Clear();
        return restoreFailed;
    }

    private bool EndPreviewSession()
    {
        isPreviewing = false;
        isPaused = false;
        ResetPauseSelectionGate();
        bool restoreFailed = RestoreParticleSystems();
        RestoreSceneViewVisibility();
        return restoreFailed;
    }

    private void ApplySceneViewVisibility()
    {
        sceneViewVisibilityStates.Clear();
        SceneView[] sceneViews = Resources.FindObjectsOfTypeAll<SceneView>();
        for (int i = 0; i < sceneViews.Length; i++)
        {
            SceneView sceneView = sceneViews[i];
            if (sceneView == null || sceneView.sceneViewState == null)
            {
                continue;
            }

            sceneViewVisibilityStates.Add(new SceneViewVisibilityState
            {
                SceneView = sceneView,
                FxEnabled = sceneView.sceneViewState.fxEnabled,
                ShowParticleSystems = sceneView.sceneViewState.showParticleSystems
            });

            sceneView.sceneViewState.fxEnabled = true;
            sceneView.sceneViewState.showParticleSystems = true;
        }

        SceneView.RepaintAll();
    }

    private void EnforceSceneViewVisibility()
    {
        for (int i = 0; i < sceneViewVisibilityStates.Count; i++)
        {
            SceneViewVisibilityState state = sceneViewVisibilityStates[i];
            if (state.SceneView == null || state.SceneView.sceneViewState == null)
            {
                continue;
            }

            state.SceneView.sceneViewState.fxEnabled = true;
            state.SceneView.sceneViewState.showParticleSystems = true;
        }
    }

    private void RestoreSceneViewVisibility()
    {
        if (sceneViewVisibilityStates.Count == 0)
        {
            return;
        }

        for (int i = 0; i < sceneViewVisibilityStates.Count; i++)
        {
            SceneViewVisibilityState state = sceneViewVisibilityStates[i];
            if (state.SceneView == null || state.SceneView.sceneViewState == null)
            {
                continue;
            }

            state.SceneView.sceneViewState.fxEnabled = state.FxEnabled;
            state.SceneView.sceneViewState.showParticleSystems = state.ShowParticleSystems;
        }

        sceneViewVisibilityStates.Clear();
        SceneView.RepaintAll();
    }
    private static void AddFromRoot(
        GameObject root,
        List<ParticleSystem> eligibleSystems,
        ref int skipped)
    {
        if (root == null)
        {
            return;
        }

        ParticleSystem[] systems = root.GetComponentsInChildren<ParticleSystem>(true);

        for (int i = 0; i < systems.Length; i++)
        {
            ParticleSystem particleSystem = systems[i];
            if (!IsEligible(particleSystem))
            {
                skipped++;
                continue;
            }

            eligibleSystems.Add(particleSystem);
        }
    }

    private static bool IsEligible(ParticleSystem particleSystem)
    {
        if (particleSystem == null || !particleSystem.gameObject.activeInHierarchy)
        {
            return false;
        }

        ParticleSystemRenderer particleRenderer = particleSystem.GetComponent<ParticleSystemRenderer>();
        return particleRenderer != null &&
               particleRenderer.enabled &&
               !particleRenderer.forceRenderingOff;
    }

    private bool CanPausePreview()
    {
        return isPreviewing &&
               pauseSelectionReady &&
               pauseSelectionEligibleSetKey == eligibleSetKey &&
               IsCurrentSelectionEligibleSystems();
    }

    private bool IsCurrentSelectionEligibleSystems()
    {
        if (particleSystems.Count == 0 || Selection.objects.Length != particleSystems.Count)
        {
            return false;
        }

        List<GameObject> expectedObjects = new List<GameObject>(particleSystems.Count);
        for (int i = 0; i < particleSystems.Count; i++)
        {
            ParticleSystem particleSystem = particleSystems[i];
            if (particleSystem == null)
            {
                return false;
            }

            expectedObjects.Add(particleSystem.gameObject);
        }

        for (int i = 0; i < Selection.objects.Length; i++)
        {
            GameObject selectedObject = Selection.objects[i] as GameObject;
            if (selectedObject == null || !expectedObjects.Remove(selectedObject))
            {
                return false;
            }
        }

        return expectedObjects.Count == 0;
    }

    private void ResetPauseSelectionGate()
    {
        pauseSelectionReady = false;
        pendingPauseSelectionArm = false;
        pauseSelectionEligibleSetKey = string.Empty;
    }
    private void SelectEligibleSystems()
    {
        List<GameObject> objects = new List<GameObject>(particleSystems.Count);
        for (int i = 0; i < particleSystems.Count; i++)
        {
            ParticleSystem particleSystem = particleSystems[i];
            if (particleSystem != null)
            {
                objects.Add(particleSystem.gameObject);
            }
        }

        string selectedEligibleSetKey = eligibleSetKey;
        Selection.objects = objects.ToArray();
        pauseSelectionReady = objects.Count > 0;
        pendingPauseSelectionArm = pauseSelectionReady;
        pauseSelectionEligibleSetKey = pauseSelectionReady ? selectedEligibleSetKey : string.Empty;
        EditorApplication.delayCall += () => ArmPauseSelectionGateAfterSelection(selectedEligibleSetKey);
        SetStatus($"Selected {objects.Count} eligible Particle System object(s).", MessageType.Info);
        Repaint();
    }

    private void ArmPauseSelectionGateAfterSelection(string selectedEligibleSetKey)
    {
        if (selectedEligibleSetKey != eligibleSetKey || particleSystems.Count == 0 || !IsCurrentSelectionEligibleSystems())
        {
            ResetPauseSelectionGate();
            Repaint();
            return;
        }

        pauseSelectionReady = true;
        pendingPauseSelectionArm = false;
        pauseSelectionEligibleSetKey = selectedEligibleSetKey;
        Repaint();
    }

    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode && (isPreviewing || isPaused))
        {
            bool restoreFailed = EndPreviewSession();
            RefreshContext(true);
            SetStatus(
                restoreFailed
                    ? "Preview stopped before entering Play Mode, but one or more Particle Systems could not be fully restored. See Console for details."
                    : "Preview stopped before entering Play Mode.",
                MessageType.Info);
        }
    }

    private void OnSelectionChange()
    {
        if (pendingPauseSelectionArm)
        {
            Repaint();
            return;
        }

        if (pauseSelectionReady && !IsCurrentSelectionEligibleSystems())
        {
            ResetPauseSelectionGate();
            EnforceSceneViewVisibility();

            if (isPaused)
            {
                isPaused = false;
                lastUpdateTime = EditorApplication.timeSinceStartup;
                SetStatus("Preview resumed because the eligible Particle System selection changed.", MessageType.Info);
            }
        }

        Repaint();
    }

    private void OnHierarchyChanged()
    {
        ScheduleContextEvaluation();
    }

    private void OnSceneOpened(Scene scene, OpenSceneMode mode)
    {
        ScheduleContextEvaluation();
    }

    private void OnSceneClosed(Scene scene)
    {
        ScheduleContextEvaluation();
    }

    private void OnNewSceneCreated(Scene scene, NewSceneSetup setup, NewSceneMode mode)
    {
        ScheduleContextEvaluation();
    }

    private void OnActiveSceneChangedInEditMode(Scene previousScene, Scene newScene)
    {
        ScheduleContextEvaluation();
    }

    private void OnPrefabStageChanged(PrefabStage prefabStage)
    {
        ScheduleContextEvaluation();
    }

    private void ScheduleContextEvaluation()
    {
        if (pendingContextEvaluation)
        {
            return;
        }

        pendingContextEvaluation = true;
        EditorApplication.delayCall += EvaluatePendingContextChange;
    }

    private void EvaluatePendingContextChange()
    {
        if (!pendingContextEvaluation)
        {
            return;
        }

        pendingContextEvaluation = false;
        ContextScanResult scan = ScanContext();
        bool contextChanged = scan.ContextKey != contextKey;
        bool eligibleSetChanged = scan.EligibleSetKey != eligibleSetKey;

        if (contextChanged)
        {
            if (isPreviewing || isPaused)
            {
                bool restoreFailed = EndPreviewSession();
                ApplyScan(scan);
                SetStatus(
                    restoreFailed
                        ? "Preview stopped because the context changed, but one or more Particle Systems could not be fully restored. See Console for details."
                        : "Preview stopped because the context changed.",
                    MessageType.Info);
            }
            else
            {
                ApplyScan(scan);
                SetIdleStatus();
            }

            Repaint();
            return;
        }

        if (eligibleSetChanged)
        {
            if (isPreviewing || isPaused)
            {
                bool restoreFailed = EndPreviewSession();
                ApplyScan(scan);
                SetStatus(
                    restoreFailed
                        ? "Preview stopped because the available Particle Systems changed, but one or more Particle Systems could not be fully restored. See Console for details."
                        : "Preview stopped because the available Particle Systems changed.",
                    MessageType.Info);
            }
            else
            {
                ApplyScan(scan);
                SetIdleStatus();
            }

            Repaint();
            return;
        }

        if (!isPreviewing && !isPaused)
        {
            ApplyScan(scan);
            SetIdleStatus();
            Repaint();
        }
    }

    private void StopPreviewBecauseAvailableSystemsChanged()
    {
        bool restoreFailed = EndPreviewSession();
        RefreshContext(true);
        SetStatus(
            restoreFailed
                ? "Preview stopped because the available Particle Systems changed, but one or more Particle Systems could not be fully restored. See Console for details."
                : "Preview stopped because the available Particle Systems changed.",
            MessageType.Info);
        SceneView.RepaintAll();
        Repaint();
    }

    private void SetIdleStatus()
    {
        if (particleSystems.Count == 0)
        {
            SetStatus("No eligible Particle Systems found in the current context.", MessageType.Info);
            return;
        }

        SetStatus($"Found {particleSystems.Count} eligible Particle System(s).", MessageType.Info);
    }

    private void SetRestoreFailureStatus()
    {
        SetStatus(
            "Preview stopped, but one or more Particle Systems could not be fully restored. See Console for details.",
            MessageType.Warning);
    }

    private void SetStatus(string message, MessageType messageType)
    {
        statusMessage = message;
        statusType = messageType;
    }

    private void UpdateSignaturesFromCurrentSystems()
    {
        eligibleSetKey = BuildEligibleSetKey(particleSystems);
    }

    private static string BuildEligibleSetKey(List<ParticleSystem> systems)
    {
        List<int> ids = new List<int>(systems.Count);
        for (int i = 0; i < systems.Count; i++)
        {
            ParticleSystem particleSystem = systems[i];
            if (particleSystem != null)
            {
                ids.Add(particleSystem.GetInstanceID());
            }
        }

        ids.Sort();
        return string.Join(",", ids.ConvertAll(id => id.ToString()).ToArray());
    }
}









