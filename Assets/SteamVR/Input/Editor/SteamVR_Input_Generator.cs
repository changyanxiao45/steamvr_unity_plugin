﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using UnityEditor;
using UnityEngine;

using System.CodeDom;
using Microsoft.CSharp;
using System.IO;
using System.CodeDom.Compiler;

using System.Reflection;
using System.Linq.Expressions;
using UnityEditor.SceneManagement;

#pragma warning disable 0219 // variable assigned but not used.

public static class SteamVR_Input_Generator
{
    public const string steamVRInputPathKey = "SteamVR_Input_Path";
    public const string steamVRInputSubFolderKey = "SteamVR_Input_ActionsFolder";
    public const string steamVRInputActionsClassKey = "SteamVR_Input_ActionsClassFilename";
    public const string steamVRInputActionSetsClassKey = "SteamVR_Input_ActionSetsClassFilename";
    public const string steamVRInputOverwriteBuildKey = "SteamVR_Input_OverwriteBuild";
    public const string steamVRInputDeleteUnusedKey = "SteamVR_Input_DeleteUnused";

    private const string defaultActionsSubFolder = "SteamVR_Input_Actions";
    private const string actionSetClassNamePrefix = "SteamVR_Input_ActionSet_";

    private const string generationStepKey = "SteamVR_Input_GenerationStep";
    private const string generationTryKey = "SteamVR_Input_GenerationTry";

    private const string progressBarTitle = "SteamVR Input Generation";
    enum GenerationSteps
    {
        None,
        GeneratingSetClasses,
        GeneratingActions,
        CreatingScriptableObjects,
        AssigningDefaults,
        Complete,
    }

    private static void SetNextGenerationStep(GenerationSteps step, bool refresh = true)
    {
        if (EditorPrefs.HasKey(generationStepKey))
        {
            if (EditorPrefs.HasKey(generationTryKey))
            {
                GenerationSteps lastStep = (GenerationSteps)EditorPrefs.GetInt(generationStepKey);

                if (lastStep == step)
                {
                    int tryNumber = EditorPrefs.GetInt(generationTryKey);
                    tryNumber++;

                    EditorPrefs.SetInt(generationTryKey, tryNumber);
                }
                else
                {
                    EditorPrefs.SetInt(generationTryKey, 0);
                }
            }
            else
            {
                EditorPrefs.SetInt(generationTryKey, 0);
            }
        }
        else
        {
            EditorPrefs.SetInt(generationTryKey, 0);
        }

        EditorPrefs.SetInt(generationStepKey, (int)step);

        if (refresh)
            ForceAssetDatabaseRefresh();
    }

    private static void SetGenerationStepBegun()
    {
        int tryNumber = EditorPrefs.GetInt(generationTryKey);
        if (tryNumber == -1)
            tryNumber = 0;
        tryNumber++;

        if (tryNumber > 1)
        {
            Debug.LogError("[SteamVR] There was an error in input generation. Aborting.");
            CancelGeneration();
        }

        EditorPrefs.SetInt(generationTryKey, tryNumber);
    }

    private static void ForceAssetDatabaseRefresh()
    {
        MonoScript[] monoScripts = MonoImporter.GetAllRuntimeMonoScripts();

        Type steamVRInputType = typeof(SteamVR_Input);
        MonoScript monoScript = monoScripts.FirstOrDefault(script => script.GetClass() == steamVRInputType);
        string path = AssetDatabase.GetAssetPath(monoScript);
        MonoImporter steamVRInputImporter = ((MonoImporter)MonoImporter.GetAtPath(path));
        steamVRInputImporter.SaveAndReimport();

        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
    }

    public static void CancelGeneration()
    {
        EditorPrefs.SetInt(generationTryKey, -1);
        EditorPrefs.SetInt(generationStepKey, 0);
    }

    public static void BeginGeneration()
    {

        string currentPath = Application.dataPath;
        int lastIndex = currentPath.LastIndexOf('/');
        currentPath = currentPath.Remove(lastIndex, currentPath.Length - lastIndex);

        SteamVR_Input_EditorWindow.SetProgressBarText("Beginning generation...", 0);

        GenerationStep_CreateActionSetClasses();
    }


    public static void OnEditorUpdate()
    {
        int tryNumber = -1;
        if (EditorPrefs.HasKey(generationTryKey))
            tryNumber = EditorPrefs.GetInt(generationTryKey);

        if (tryNumber == 0)
        {
            CheckForNextStep();
        }
    }

    [UnityEditor.Callbacks.DidReloadScripts]
    private static void OnScriptsReloaded()
    {
#if UNITY_2017_1_OR_NEWER
#else
        CheckForNextStep();
#endif
        SteamVR_Settings.VerifyScriptableObject();
    }

    private static void CheckForNextStep()
    {
        if (EditorPrefs.HasKey(generationStepKey))
        {
            GenerationSteps step = (GenerationSteps)EditorPrefs.GetInt(generationStepKey);
            int tryNumber = EditorPrefs.GetInt(generationTryKey);

            if (tryNumber > 1)
            {
                Debug.LogError("[SteamVR] There was an error in input generation. Aborting.");
                CancelGeneration();
            }

            if (step != GenerationSteps.None)
                ExecuteNextStep(step);
        }
    }

    private static void ExecuteNextStep(GenerationSteps currentStep)
    {
        switch (currentStep)
        {
            case GenerationSteps.GeneratingSetClasses:
                GenerationStep_CreateHelperClasses();
                break;
            case GenerationSteps.GeneratingActions:
                GenerationStep_CreateScriptableObjects();
                break;
            case GenerationSteps.CreatingScriptableObjects:
                GenerationStep_AssignDefaultActions();
                break;
            case GenerationSteps.AssigningDefaults:
                GenerationStep_Complete();
                break;
        }
    }

    private static void GenerationStep_CreateActionSetClasses()
    {
        SetGenerationStepBegun();

        SteamVR_Input_EditorWindow.SetProgressBarText("Generating action set classes...", 0.25f);

        SteamVR_Input.InitializeFile();

        CreateActionsSubFolder();

        List<CodeTypeDeclaration> setClasses = GenerateActionSetClasses();

        SetNextGenerationStep(GenerationSteps.GeneratingSetClasses);
    }

    private static void GenerationStep_CreateHelperClasses()
    {
        SetGenerationStepBegun();

        SteamVR_Input_EditorWindow.SetProgressBarText("Generating actions and actionsets classes...", 0.5f);

        SteamVR_Input.InitializeFile();

        string actionsFilename = EditorPrefs.GetString(steamVRInputActionsClassKey);
        string actionSetsFilename = EditorPrefs.GetString(steamVRInputActionSetsClassKey);

        GenerateActionHelpers(actionsFilename);
        GenerateActionSetsHelpers(actionSetsFilename);

        string actionsFullpath = Path.Combine(GetClassPath(), actionsFilename + ".cs");
        string actionSetsFullpath = Path.Combine(GetClassPath(), actionsFilename + ".cs");

        Debug.LogFormat("[SteamVR] Created input script main classes: {0} and {1}", actionsFullpath, actionSetsFullpath);

        SetNextGenerationStep(GenerationSteps.GeneratingActions);
    }

    private static void GenerationStep_CreateScriptableObjects()
    {
        SetGenerationStepBegun();

        SteamVR_Input_EditorWindow.SetProgressBarText("Generating scriptable objects...", 0.75f);

        SteamVR_Input.InitializeFile();

        CreateScriptableObjects();

        bool deleteUnused = EditorPrefs.GetBool(SteamVR_Input_Generator.steamVRInputDeleteUnusedKey);
        if (deleteUnused)
            DeleteUnusedScriptableObjects();

        AssetDatabase.SaveAssets();

        SetNextGenerationStep(GenerationSteps.CreatingScriptableObjects);
    }

    private static void GenerationStep_AssignDefaultActions()
    {
        SetGenerationStepBegun();

        SteamVR_Input_EditorWindow.SetProgressBarText("Assigning default actions to MonoBehaviours...", 0.85f);

        SteamVR_Input.InitializeFile();

        AssignDefaultsInPrefabs();

        AssignDefaultsInBuiltScenes();

        AssetDatabase.SaveAssets();

        SetNextGenerationStep(GenerationSteps.AssigningDefaults);
    }

    private static Dictionary<Type, bool> hasDefaultAttributeCache = new Dictionary<Type, bool>();
    private static Type defaultInputActionType = typeof(DefaultInputAction);

    private static void AssignDefaultsInPrefabs()
    {
        string[] prefabs = GetAllPrefabPaths();
        for (int prefabIndex = 0; prefabIndex < prefabs.Length; prefabIndex++)
        {
            string prefabPath = prefabs[prefabIndex];

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);

            MonoBehaviour[] behaviours = prefab.GetComponentsInChildren<MonoBehaviour>();
            for (int index = 0; index < behaviours.Length; index++)
            {
                MonoBehaviour behaviour = behaviours[index];
                if (behaviour == null)
                    continue;

                Type behaviourType = behaviour.GetType();
                if (hasDefaultAttributeCache.ContainsKey(behaviourType) == false)
                {
                    bool containsProperty = behaviourType.GetProperties().Any(prop => prop.IsDefined(defaultInputActionType, false));
                    bool containsField = behaviourType.GetFields().Any(field => field.IsDefined(defaultInputActionType, false));

                    hasDefaultAttributeCache[behaviourType] = containsProperty || containsField;
                }

                if (hasDefaultAttributeCache[behaviourType] == true)
                {
                    var properties = behaviourType.GetProperties().Where(prop => prop.IsDefined(defaultInputActionType, false));
                    foreach (var property in properties)
                    {
                        var attributes = property.GetCustomAttributes(defaultInputActionType, false);
                        foreach (var attribute in attributes)
                        {
                            DefaultInputAction defaultAttribute = (DefaultInputAction)attribute;
                            defaultAttribute.AssignDefault(property, behaviour);
                        }
                    }

                    var fields = behaviourType.GetFields().Where(field => field.IsDefined(defaultInputActionType, false));
                    foreach (var field in fields)
                    {
                        var attributes = field.GetCustomAttributes(defaultInputActionType, false);
                        foreach (var attribute in attributes)
                        {
                            DefaultInputAction defaultAttribute = (DefaultInputAction)attribute;
                            defaultAttribute.AssignDefault(field, behaviour);
                        }
                    }

                    EditorUtility.SetDirty(behaviour);
                }
            }
        }
    }

    private static void AssignDefaultsInBuiltScenes()
    {
        if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            EditorSceneManager.SaveOpenScenes();

        var activeScene = EditorSceneManager.GetActiveScene();
        string activeScenePath = activeScene.path;

        List<string> processedScenes = new List<string>();

        AssignDefaultsInScene();
        EditorSceneManager.SaveOpenScenes();

        processedScenes.Add(activeScene.path);

        string[] interactionsSceneGUIDs = AssetDatabase.FindAssets("Interactions_Example");

        for (int sceneIndex = 0; sceneIndex < interactionsSceneGUIDs.Length; sceneIndex++)
        {
            string path = AssetDatabase.GUIDToAssetPath(interactionsSceneGUIDs[sceneIndex]);
            if (processedScenes.Contains(path) == false)
            {
                EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                AssignDefaultsInScene();
                EditorSceneManager.SaveOpenScenes();
                processedScenes.Add(path);
            }
        }

        for (int sceneIndex = 0; sceneIndex < EditorBuildSettings.scenes.Length; sceneIndex++)
        {
            if (EditorBuildSettings.scenes[sceneIndex].enabled)
            {
                string scenePath = EditorBuildSettings.scenes[sceneIndex].path;
                if (string.IsNullOrEmpty(scenePath) == false)
                {
                    if (processedScenes.Contains(scenePath) == false)
                    {
                        EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                        AssignDefaultsInScene();
                        EditorSceneManager.SaveOpenScenes();
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(activeScenePath) == false)
        {
            EditorSceneManager.OpenScene(activeScenePath, OpenSceneMode.Single);
            Debug.Log("Opened previous scene: " + activeScenePath);
        }
    }

    private static void AssignDefaultsInScene()
    {
        MonoBehaviour[] behaviours = GameObject.FindObjectsOfType<MonoBehaviour>();
        for (int behaviourIndex = 0; behaviourIndex < behaviours.Length; behaviourIndex++)
        {
            MonoBehaviour behaviour = behaviours[behaviourIndex];
            Type behaviourType = behaviour.GetType();
            if (behaviour != null)
            {
                if (hasDefaultAttributeCache.ContainsKey(behaviourType) == false)
                {
                    bool containsProperty = behaviourType.GetProperties().Any(prop => prop.IsDefined(defaultInputActionType, false));
                    bool containsField = behaviourType.GetFields().Any(field => field.IsDefined(defaultInputActionType, false));

                    hasDefaultAttributeCache[behaviourType] = containsProperty || containsField;
                }

                if (hasDefaultAttributeCache[behaviourType] == true)
                {
                    var properties = behaviourType.GetProperties().Where(prop => prop.IsDefined(defaultInputActionType, false));
                    foreach (var property in properties)
                    {
                        var attributes = property.GetCustomAttributes(defaultInputActionType, false);
                        foreach (var attribute in attributes)
                        {
                            DefaultInputAction defaultAttribute = (DefaultInputAction)attribute;
                            defaultAttribute.AssignDefault(property, behaviour);
                            Debug.Log("[SteamVR] Assigning default action for: " + behaviour.gameObject.name + " / " + defaultAttribute.actionName);
                        }
                    }

                    var fields = behaviourType.GetFields().Where(field => field.IsDefined(defaultInputActionType, false));
                    foreach (var field in fields)
                    {
                        var attributes = field.GetCustomAttributes(defaultInputActionType, false);
                        foreach (var attribute in attributes)
                        {
                            DefaultInputAction defaultAttribute = (DefaultInputAction)attribute;
                            defaultAttribute.AssignDefault(field, behaviour);
                            Debug.Log("[SteamVR] Assigning default action for: " + behaviour.gameObject.name + " / " + defaultAttribute.actionName);
                        }
                    }

                    EditorUtility.SetDirty(behaviour);
                    EditorSceneManager.MarkAllScenesDirty();
                }
            }
        }
    }

    private static string[] GetAllPrefabPaths()
    {
        string[] temp = AssetDatabase.GetAllAssetPaths();
        List<string> result = new List<string>();
        foreach (string s in temp)
        {
            if (s.Contains(".prefab")) result.Add(s);
        }
        return result.ToArray();
    }

    private static void DeleteUnusedScriptableObjects()
    {
        string folderPath = GetSubFolderPath();

        string[] files = Directory.GetFiles(folderPath);

        for (int fileIndex = 0; fileIndex < files.Length; fileIndex++)
        {
            FileInfo file = new FileInfo(files[fileIndex]);

            if (file.Name.EndsWith(".asset") || file.Name.EndsWith(".asset.meta"))
            {
                bool isSet = false;
                if (SteamVR_Input.actionFile.action_sets.Any(set => set.codeFriendlyName + ".asset" == file.Name || set.codeFriendlyName + ".asset.meta" == file.Name))
                {
                    isSet = true;
                }
                else if (SteamVR_Input.actionFile.action_sets.Any(set => GetActionListClassName(set, SteamVR_Input_ActionDirections.In) + ".cs" == file.Name || GetActionListClassName(set, SteamVR_Input_ActionDirections.In) + ".cs.meta" == file.Name))
                {
                    isSet = true;
                }
                else if (SteamVR_Input.actionFile.action_sets.Any(set => GetActionListClassName(set, SteamVR_Input_ActionDirections.Out) + ".cs" == file.Name || GetActionListClassName(set, SteamVR_Input_ActionDirections.Out) + ".cs.meta" == file.Name))
                {
                    isSet = true;
                }
                else if (SteamVR_Input.actionFile.action_sets.Any(set => GetActionListClassName(set, SteamVR_Input_ActionDirections.In) + ".asset" == file.Name || GetActionListClassName(set, SteamVR_Input_ActionDirections.In) + ".asset.meta" == file.Name))
                {
                    isSet = true;
                }
                else if (SteamVR_Input.actionFile.action_sets.Any(set => GetActionListClassName(set, SteamVR_Input_ActionDirections.Out) + ".asset" == file.Name || GetActionListClassName(set, SteamVR_Input_ActionDirections.Out) + ".asset.meta" == file.Name))
                {
                    isSet = true;
                }


                bool isAction = false;
                if (SteamVR_Input.actionFile.actions.Any(action => action.codeFriendlyName + ".asset" == file.Name || action.codeFriendlyName + ".asset.meta" == file.Name))
                {
                    isAction = true;
                }

                if (isSet == false && isAction == false)
                {
                    file.IsReadOnly = false;

                    bool confirm = EditorUtility.DisplayDialog("Delete unused file", "Would you like to delete the unused input file: " + file.Name + "?", "Delete", "Cancel");
                    if (confirm)
                    {
                        file.Delete();
                    }
                }
            }

        }
    }

    private static void GenerationStep_Complete()
    {
        SetNextGenerationStep(GenerationSteps.None, false);
        EditorPrefs.SetInt(generationTryKey, -1);

        SteamVR_Input_EditorWindow.ClearProgressBar();

        Debug.Log("SteamVR Input Generation complete!");
    }

    private static void CreateActionsSubFolder()
    {
        string folderPath = GetSubFolderPath();
        if (Directory.Exists(folderPath) == false)
        {
            Directory.CreateDirectory(folderPath);
        }
    }

    private static void CreateScriptableObjects()
    {
        List<string> setNames = new List<string>();
        List<SteamVR_Input_ActionSet> setObjects = new List<SteamVR_Input_ActionSet>();
        List<string> actionNames = new List<string>();
        List<SteamVR_Input_Action> actionObjects = new List<SteamVR_Input_Action>();

        for (int actionSetIndex = 0; actionSetIndex < SteamVR_Input.actionFile.action_sets.Count; actionSetIndex++)
        {
            SteamVR_Input_ActionFile_ActionSet actionSet = SteamVR_Input.actionFile.action_sets[actionSetIndex];
            SteamVR_Input_ActionSet actionSetAsset = CreateScriptableActionSet(actionSet);

            string shortName = GetValidIdentifier(actionSet.shortName);

            string codeFriendlyInstanceName = GetCodeFriendlyInstanceName(shortName);

            setNames.Add(codeFriendlyInstanceName);
            setObjects.Add(actionSetAsset);

            foreach (var action in actionSetAsset.allActions)
            {
                actionNames.Add(GetCodeFriendlyInstanceName(action.name));
                actionObjects.Add(action);
            }
        }

        SteamVR_Input_References.instance.actionSetNames = setNames.ToArray();
        SteamVR_Input_References.instance.actionSetObjects = setObjects.ToArray();
        SteamVR_Input_References.instance.actionNames = actionNames.ToArray();
        SteamVR_Input_References.instance.actionObjects = actionObjects.ToArray();

        EditorUtility.SetDirty(SteamVR_Input_References.instance);
        AssetDatabase.SaveAssets();

        string folderPath = GetSubFolderPath();
        Debug.LogFormat("[SteamVR] Created {0} action set objects and {1} action objects in: {2}", SteamVR_Input.actionFile.action_sets.Count, SteamVR_Input.actionFile.actions.Count, folderPath);
    }

    private static SteamVR_Input_Action CreateScriptableAction(SteamVR_Input_ActionFile_Action action, SteamVR_Input_ActionSet set)
    {
        SteamVR_Input_Action asset = null;
        Type actionType = GetTypeForAction(action);

        string folderPath = GetSubFolderPath();
        string path = Path.Combine(folderPath, action.codeFriendlyName + ".asset");
        UnityEngine.Object assetAtPath = AssetDatabase.LoadAssetAtPath(path, typeof(SteamVR_Input_Action));
        SteamVR_Input_Action existingAction = null;
        if (assetAtPath != null)
        {
            existingAction = AssetDatabase.LoadAssetAtPath(path, actionType) as SteamVR_Input_Action;
            if (existingAction != null)
            {
                if (existingAction.GetType().Name != GetTypeStringForAction(action))
                {
                    //only delete the asset if it's of a different type
                    AssetDatabase.DeleteAsset(path);
                    existingAction = null;
                }
                else
                {
                    EditorUtility.SetDirty(existingAction);
                    asset = existingAction;
                }
            }
        }

        if (asset == null)
        {
            asset = (SteamVR_Input_Action)ScriptableObject.CreateInstance(GetTypeForAction(action));
        }

        asset.fullPath = action.name;
        asset.direction = action.direction;
        asset.actionSet = set;

        if (existingAction == null)
            AssetDatabase.CreateAsset(asset, path);
        return asset;
    }

    private static SteamVR_Input_ActionSet CreateScriptableActionSet(SteamVR_Input_ActionFile_ActionSet actionSet)
    {
        SteamVR_Input_ActionSet asset = null;
        Type setType = typeof(SteamVR_Input_Action).Assembly.GetType(GetSetClassName(actionSet));

        string folderPath = GetSubFolderPath();
        string path = Path.Combine(folderPath, actionSet.codeFriendlyName + ".asset");
        UnityEngine.Object assetAtPath = AssetDatabase.LoadAssetAtPath(path, typeof(SteamVR_Input_Action));
        SteamVR_Input_ActionSet existingSet = null;
        if (assetAtPath != null)
        {
            existingSet = AssetDatabase.LoadAssetAtPath(path, setType) as SteamVR_Input_ActionSet;
            if (existingSet != null)
            {
                if (existingSet.usage != actionSet.usage)
                {
                    //only delete the asset if it's of a different usage
                    AssetDatabase.DeleteAsset(path);
                    existingSet = null;
                }
                else
                {
                    asset = existingSet;
                    EditorUtility.SetDirty(existingSet);
                }
            }
        }

        if (asset == null)
        {
            asset = ScriptableObject.CreateInstance(setType) as SteamVR_Input_ActionSet;
        }

        asset.fullPath = actionSet.name;
        asset.usage = actionSet.usage;

        SteamVR_Input_Action_List inList = null;
        SteamVR_Input_Action_List outList = null;

        List<SteamVR_Input_Action> actionsList = new List<SteamVR_Input_Action>();

        if (actionSet.actionsInList.Count > 0)
        {
            inList = CreateScriptableActionList(actionSet, asset, SteamVR_Input_ActionDirections.In);
            actionsList.AddRange(inList.actions);

            FieldInfo inField = setType.GetField(inActionsFieldName);
            inField.SetValue(asset, inList);
        }

        if (actionSet.actionsOutList.Count > 0)
        {
            outList = CreateScriptableActionList(actionSet, asset, SteamVR_Input_ActionDirections.Out);
            actionsList.AddRange(outList.actions);

            FieldInfo outField = setType.GetField(outActionsFieldName);
            outField.SetValue(asset, outList);
        }

        asset.allActions = actionsList.ToArray();
        asset.nonVisualInActions = actionsList.Where(action => action is SteamVR_Input_Action_In && (action is SteamVR_Input_Action_Pose == false) && (action is SteamVR_Input_Action_Skeleton == false)).Cast<SteamVR_Input_Action_In>().ToArray();
        asset.visualActions = actionsList.Where(action => action is SteamVR_Input_Action_Pose || action is SteamVR_Input_Action_Skeleton).Cast<SteamVR_Input_Action_In>().ToArray();
        asset.poseActions = actionsList.Where(action => action is SteamVR_Input_Action_Pose).Cast<SteamVR_Input_Action_Pose>().ToArray();
        asset.skeletonActions = actionsList.Where(action => action is SteamVR_Input_Action_Skeleton).Cast<SteamVR_Input_Action_Skeleton>().ToArray();
        asset.outActionArray = actionsList.Where(action => action is SteamVR_Input_Action_Out).Cast<SteamVR_Input_Action_Out>().ToArray();

        if (existingSet == null)
            AssetDatabase.CreateAsset(asset, path);

        return asset;
    }

    private static SteamVR_Input_Action_List CreateScriptableActionList(SteamVR_Input_ActionFile_ActionSet fileActionSet, SteamVR_Input_ActionSet actionSetAsset, SteamVR_Input_ActionDirections direction)
    {
        SteamVR_Input_Action_List listAsset = null;
        List<SteamVR_Input_ActionFile_Action> actions = null;
        string listClassName = GetActionListClassName(fileActionSet, direction);

        if (direction == SteamVR_Input_ActionDirections.In)
        {
            actions = fileActionSet.actionsInList;
        }

        if (direction == SteamVR_Input_ActionDirections.Out)
        {
            actions = fileActionSet.actionsOutList;
        }

        string folderPath = GetSubFolderPath();
        string path = Path.Combine(folderPath, listClassName + ".asset");
        UnityEngine.Object assetAtPath = AssetDatabase.LoadAssetAtPath(path, typeof(SteamVR_Input_Action));
        SteamVR_Input_Action_List existingListAsset = null;
        if (assetAtPath != null)
        {
            existingListAsset = AssetDatabase.LoadAssetAtPath<SteamVR_Input_Action_List>(path);
            if (existingListAsset != null)
            {
                listAsset = existingListAsset;
                EditorUtility.SetDirty(existingListAsset);
            }
        }

        Type listType = typeof(SteamVR_Input_Action).Assembly.GetType(listClassName);

        if (listAsset == null)
        {
            listAsset = ScriptableObject.CreateInstance(listType) as SteamVR_Input_Action_List;
        }

        listAsset.actionSet = actionSetAsset;
        listAsset.listDirection = direction;
        List<SteamVR_Input_Action> actionsList = new List<SteamVR_Input_Action>();

        foreach (var action in actions)
        {
            SteamVR_Input_Action actionAsset = CreateScriptableAction(action, actionSetAsset);
            actionsList.Add(actionAsset);

            FieldInfo actionField = listType.GetField(action.shortName);
            actionField.SetValue(listAsset, actionAsset);
        }

        listAsset.actions = actionsList.ToArray();

        if (existingListAsset == null)
        {
            AssetDatabase.CreateAsset(listAsset, path);
        }

        return listAsset;
    }

    public static void DeleteActionClassFiles()
    {
        string actionsFilename = EditorPrefs.GetString(steamVRInputActionsClassKey);
        string actionSetsFilename = EditorPrefs.GetString(steamVRInputActionSetsClassKey);

        DeleteActionClass(actionsFilename);
        DeleteActionClass(actionSetsFilename);

        string folderPath = GetSubFolderPath();
        bool confirm = EditorUtility.DisplayDialog("Confirmation", "Are you absolutely sure you want to delete all code files in " + folderPath + "?", "Delete", "Cancel");
        if (confirm)
        {
            DeleteActionObjects("*.cs*");
        }
    }

    private static void ForceRefresh()
    {
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        /*
        Type type = Type.GetType("UnityEditorInternal.LogEntries,UnityEditor.dll");
        MethodInfo info = type.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (info != null)
            info.Invoke(null, null);
            */
    }

    public static void DeleteActionObjects(string filter)
    {
        string folderPath = GetSubFolderPath();

        string[] assets = Directory.GetFiles(folderPath, filter);

        for (int assetIndex = 0; assetIndex < assets.Length; assetIndex++)
        {
            AssetDatabase.DeleteAsset(assets[assetIndex]);
        }

        Debug.LogFormat("[SteamVR] Deleted {0} actions at path: {1}", assets.Length, folderPath);
    }

    private static void DeleteActionClass(string className)
    {
        string filePath = GetSourceFilePath(className);
        if (File.Exists(filePath) == true)
        {
            AssetDatabase.DeleteAsset(filePath);
            Debug.Log("[SteamVR] Deleted: " + filePath);
        }
        else
        {
            Debug.Log("[SteamVR] No file found at: " + filePath);
        }
    }


    private static string GetTypeStringForAction(SteamVR_Input_ActionFile_Action action)
    {
        return GetTypeForAction(action).Name;
    }

    private static Type GetTypeForAction(SteamVR_Input_ActionFile_Action action)
    {
        string actionType = action.type.ToLower();

        if (SteamVR_Input_ActionFile_ActionTypes.boolean == actionType)
        {
            return typeof(SteamVR_Input_Action_Boolean);
        }

        if (SteamVR_Input_ActionFile_ActionTypes.vector1 == actionType)
        {
            return typeof(SteamVR_Input_Action_Single);
        }

        if (SteamVR_Input_ActionFile_ActionTypes.vector2 == actionType)
        {
            return typeof(SteamVR_Input_Action_Vector2);
        }

        if (SteamVR_Input_ActionFile_ActionTypes.vector3 == actionType)
        {
            return typeof(SteamVR_Input_Action_Vector3);
        }

        if (SteamVR_Input_ActionFile_ActionTypes.pose == actionType)
        {
            return typeof(SteamVR_Input_Action_Pose);
        }

        if (SteamVR_Input_ActionFile_ActionTypes.skeleton == actionType)
        {
            return typeof(SteamVR_Input_Action_Skeleton);
        }

        if (SteamVR_Input_ActionFile_ActionTypes.vibration == actionType)
        {
            return typeof(SteamVR_Input_Action_Vibration);
        }

        throw new System.Exception("unknown type (" + action.type + ") in actions file for action: " + action.name);
    }

    private static string GetClassPath()
    {
        string path = EditorPrefs.GetString(steamVRInputPathKey);
        if (path[0] == '/' || path[0] == '\\')
            path = path.Remove(0, 1);

        return path;
    }

    private static string GetSubFolderPath()
    {
        return Path.Combine(GetClassPath(), EditorPrefs.GetString(steamVRInputSubFolderKey));
    }

    private static string GetSourceFilePath(string classname)
    {
        string sourceFileName = string.Format("{0}.cs", classname);

        return Path.Combine(GetClassPath(), sourceFileName);
    }

    private static void CreateFile(string fullPath, CodeCompileUnit compileUnit)
    {
        // Generate the code with the C# code provider.
        CSharpCodeProvider provider = new CSharpCodeProvider();

        // Build the output file name.
        string fullSourceFilePath = fullPath;
        Debug.Log("[SteamVR] Writing class to: " + fullSourceFilePath);

        string path = GetClassPath();
        string[] parts = path.Split('/');

        for (int partIndex = 0; partIndex < parts.Length - 1; partIndex++)
        {
            string directoryPath = string.Join("/", parts.Take(partIndex + 1).ToArray());
            if (Directory.Exists(directoryPath) == false)
            {
                Directory.CreateDirectory(directoryPath);
                Debug.Log("[SteamVR] Created: " + directoryPath);
            }
        }

        FileInfo file = new FileInfo(fullSourceFilePath);
        if (file.Exists)
            file.IsReadOnly = false;

        // Create a TextWriter to a StreamWriter to the output file.
        using (StreamWriter sw = new StreamWriter(fullSourceFilePath, false))
        {
            IndentedTextWriter tw = new IndentedTextWriter(sw, "    ");

            // Generate source code using the code provider.
            provider.GenerateCodeFromCompileUnit(compileUnit, tw,
                new CodeGeneratorOptions() { BracingStyle = "C" });

            // Close the output file.
            tw.Close();
        }

        Debug.Log("[SteamVR] Complete! Input class at: " + fullSourceFilePath);
    }

    private const string steamVRInstanceName = "instance";
    private const string getActionMethodParamName = "path";
    private const string skipStateUpdatesParamName = "skipStateAndEventUpdates";

    public const string instancePrefix = "instance_";
    public const string initializationMethodPrefix = "Initialize_";

    private static List<CodeTypeDeclaration> GenerateActionSetClasses()
    {
        List<CodeTypeDeclaration> setClasses = new List<CodeTypeDeclaration>();

        steamVRInputInstance = new CodeFieldReferenceExpression(new CodeTypeReferenceExpression(typeof(SteamVR_Input).Name), steamVRInstanceName);

        for (int actionSetIndex = 0; actionSetIndex < SteamVR_Input.actionFile.action_sets.Count; actionSetIndex++)
        {
            SteamVR_Input_ActionFile_ActionSet actionSet = SteamVR_Input.actionFile.action_sets[actionSetIndex];

            CodeTypeDeclaration inClass = null;
            if (actionSet.actionsInList.Count > 0)
            {
                inClass = CreateActionListClass(actionSet, SteamVR_Input_ActionDirections.In);
                setClasses.Add(inClass);
            }

            CodeTypeDeclaration outClass = null;
            if (actionSet.actionsOutList.Count > 0)
            {
                outClass = CreateActionListClass(actionSet, SteamVR_Input_ActionDirections.Out);
                setClasses.Add(outClass);
            }

            CodeTypeDeclaration setClass = CreateActionSetClass(actionSet, inClass, outClass);

            setClasses.Add(setClass);
        }

        return setClasses;
    }

    private static void GenerateActionHelpers(string actionsClassFileName)
    {
        CodeCompileUnit compileUnit = new CodeCompileUnit();

        CodeTypeDeclaration inputClass = CreatePartialInputClass(compileUnit);

        steamVRInputInstance = new CodeFieldReferenceExpression(new CodeTypeReferenceExpression(typeof(SteamVR_Input).Name), steamVRInstanceName);

        CodeParameterDeclarationExpression skipStateUpdatesParameter = new CodeParameterDeclarationExpression(new CodeTypeReference(typeof(bool)), skipStateUpdatesParamName);

        CodeMemberMethod initializeMethod = CreateMethod(inputClass, SteamVR_Input_Generator_Names.initializeActionsMethodName, true);

        CodeArrayCreateExpression actionsArray = new CodeArrayCreateExpression(new CodeTypeReference(typeof(SteamVR_Input_Action)));

        CodeArrayCreateExpression actionsInArray = new CodeArrayCreateExpression(new CodeTypeReference(typeof(SteamVR_Input_Action_In)));

        CodeArrayCreateExpression actionsOutArray = new CodeArrayCreateExpression(new CodeTypeReference(typeof(SteamVR_Input_Action_Out)));

        CodeArrayCreateExpression actionsVibrationArray = new CodeArrayCreateExpression(new CodeTypeReference(typeof(SteamVR_Input_Action_Vibration)));

        CodeArrayCreateExpression actionsPoseArray = new CodeArrayCreateExpression(new CodeTypeReference(typeof(SteamVR_Input_Action_Pose)));

        CodeArrayCreateExpression actionsSkeletonArray = new CodeArrayCreateExpression(new CodeTypeReference(typeof(SteamVR_Input_Action_Skeleton)));

        CodeArrayCreateExpression actionsBooleanArray = new CodeArrayCreateExpression(new CodeTypeReference(typeof(SteamVR_Input_Action_Boolean)));

        CodeArrayCreateExpression actionsSingleArray = new CodeArrayCreateExpression(new CodeTypeReference(typeof(SteamVR_Input_Action_Single)));

        CodeArrayCreateExpression actionsVector2Array = new CodeArrayCreateExpression(new CodeTypeReference(typeof(SteamVR_Input_Action_Vector2)));

        CodeArrayCreateExpression actionsVector3Array = new CodeArrayCreateExpression(new CodeTypeReference(typeof(SteamVR_Input_Action_Vector3)));

        CodeArrayCreateExpression actionsNonPoseNonSkeletonArray = new CodeArrayCreateExpression(new CodeTypeReference(typeof(SteamVR_Input_Action_In)));


        //add the getaction method to
        CodeMemberMethod actionsInitMethod = CreateMethod(inputClass, initializationMethodPrefix + SteamVR_Input_Generator_Names.actionsFieldName, false);


        MethodInfo initializeActionMethodInfo = GetMethodInfo<SteamVR_Input_Action>(set => set.Initialize());
        MethodInfo updateActionMethodInfo = GetMethodInfo<SteamVR_Input_Action_In>(set => set.UpdateValue(SteamVR_Input_Input_Sources.Any));


        for (int actionIndex = 0; actionIndex < SteamVR_Input.actionFile.actions.Count; actionIndex++)
        {
            SteamVR_Input_ActionFile_Action action = SteamVR_Input.actionFile.actions[actionIndex];

            string type = GetTypeStringForAction(action);

            string codeFriendlyInstanceName = GetCodeFriendlyInstanceName(action.codeFriendlyName);

            CodeMemberField actionInstanceField = CreateInstanceField(inputClass, action.codeFriendlyName, type);

            //CodeMemberProperty actionStaticProperty = CreateStaticProperty(inputClass, action.codeFriendlyName, type, codeFriendlyInstanceName); //don't pollute static class with stuff we don't need

            actionsArray.Initializers.Add(new CodeFieldReferenceExpression(new CodeThisReferenceExpression(), codeFriendlyInstanceName));

            if (action.direction == SteamVR_Input_ActionDirections.In)
            {
                actionsInArray.Initializers.Add(new CodeFieldReferenceExpression(new CodeThisReferenceExpression(), codeFriendlyInstanceName));

                if (type == typeof(SteamVR_Input_Action_Pose).Name)
                {
                    actionsPoseArray.Initializers.Add(new CodeFieldReferenceExpression(new CodeThisReferenceExpression(), codeFriendlyInstanceName));
                }
                else if (type == typeof(SteamVR_Input_Action_Skeleton).Name)
                {
                    actionsSkeletonArray.Initializers.Add(new CodeFieldReferenceExpression(new CodeThisReferenceExpression(), codeFriendlyInstanceName));
                }
                else if (type == typeof(SteamVR_Input_Action_Boolean).Name)
                {
                    actionsBooleanArray.Initializers.Add(new CodeFieldReferenceExpression(new CodeThisReferenceExpression(), codeFriendlyInstanceName));
                }
                else if (type == typeof(SteamVR_Input_Action_Single).Name)
                {
                    actionsSingleArray.Initializers.Add(new CodeFieldReferenceExpression(new CodeThisReferenceExpression(), codeFriendlyInstanceName));
                }
                else if (type == typeof(SteamVR_Input_Action_Vector2).Name)
                {
                    actionsVector2Array.Initializers.Add(new CodeFieldReferenceExpression(new CodeThisReferenceExpression(), codeFriendlyInstanceName));
                }
                else if (type == typeof(SteamVR_Input_Action_Vector3).Name)
                {
                    actionsVector3Array.Initializers.Add(new CodeFieldReferenceExpression(new CodeThisReferenceExpression(), codeFriendlyInstanceName));
                }

                if ((type == typeof(SteamVR_Input_Action_Skeleton).Name) == false && (type == typeof(SteamVR_Input_Action_Pose).Name) == false)
                {
                    actionsNonPoseNonSkeletonArray.Initializers.Add(new CodeFieldReferenceExpression(new CodeThisReferenceExpression(), codeFriendlyInstanceName));
                }
            }
            else
            {
                actionsVibrationArray.Initializers.Add(new CodeFieldReferenceExpression(new CodeThisReferenceExpression(), codeFriendlyInstanceName));

                actionsOutArray.Initializers.Add(new CodeFieldReferenceExpression(new CodeThisReferenceExpression(), codeFriendlyInstanceName));
            }

            CodeMethodInvokeExpression initializeActionMethod = AddInstanceInvokeToMethod(initializeMethod, codeFriendlyInstanceName, initializeActionMethodInfo.Name);
        }

        AddAssignStatement(actionsInitMethod, GetCodeFriendlyInstanceName(SteamVR_Input_Generator_Names.actionsFieldName), actionsArray);
        AddAssignStatement(actionsInitMethod, GetCodeFriendlyInstanceName(SteamVR_Input_Generator_Names.actionsInFieldName), actionsInArray);
        AddAssignStatement(actionsInitMethod, GetCodeFriendlyInstanceName(SteamVR_Input_Generator_Names.actionsOutFieldName), actionsOutArray);
        AddAssignStatement(actionsInitMethod, GetCodeFriendlyInstanceName(SteamVR_Input_Generator_Names.actionsVibrationFieldName), actionsVibrationArray);
        AddAssignStatement(actionsInitMethod, GetCodeFriendlyInstanceName(SteamVR_Input_Generator_Names.actionsPoseFieldName), actionsPoseArray);
        AddAssignStatement(actionsInitMethod, GetCodeFriendlyInstanceName(SteamVR_Input_Generator_Names.actionsBooleanFieldName), actionsBooleanArray);
        AddAssignStatement(actionsInitMethod, GetCodeFriendlyInstanceName(SteamVR_Input_Generator_Names.actionsSingleFieldName), actionsSingleArray);
        AddAssignStatement(actionsInitMethod, GetCodeFriendlyInstanceName(SteamVR_Input_Generator_Names.actionsVector2FieldName), actionsVector2Array);
        AddAssignStatement(actionsInitMethod, GetCodeFriendlyInstanceName(SteamVR_Input_Generator_Names.actionsVector3FieldName), actionsVector3Array);
        AddAssignStatement(actionsInitMethod, GetCodeFriendlyInstanceName(SteamVR_Input_Generator_Names.actionsSkeletonFieldName), actionsSkeletonArray);
        AddAssignStatement(actionsInitMethod, GetCodeFriendlyInstanceName(SteamVR_Input_Generator_Names.actionsNonPoseNonSkeletonIn), actionsNonPoseNonSkeletonArray);

        initializeMethod.Statements.Add(new CodeMethodInvokeExpression(steamVRInputInstance, actionsInitMethod.Name));


        // Build the output file name.
        string fullSourceFilePath = GetSourceFilePath(actionsClassFileName);
        CreateFile(fullSourceFilePath, compileUnit);
    }

    private static void GenerateActionSetsHelpers(string actionSetsClassFileName)
    {
        CodeCompileUnit compileUnit = new CodeCompileUnit();

        CodeTypeDeclaration inputClass = CreatePartialInputClass(compileUnit);


        CodeMemberMethod initializeMethod = CreateMethod(inputClass, SteamVR_Input_Generator_Names.initializeActionSetsMethodName, true);

        CodeMemberMethod actionSetsInitMethod = CreateMethod(inputClass, initializationMethodPrefix + SteamVR_Input_Generator_Names.actionSetsFieldName, false);

        CodeArrayCreateExpression actionSetsArray = new CodeArrayCreateExpression(new CodeTypeReference(typeof(SteamVR_Input_ActionSet)));

        MethodInfo initializeActionSetMethodInfo = GetMethodInfo<SteamVR_Input_ActionSet>(set => set.Initialize());

        for (int actionSetIndex = 0; actionSetIndex < SteamVR_Input.actionFile.action_sets.Count; actionSetIndex++)
        {
            SteamVR_Input_ActionFile_ActionSet actionSet = SteamVR_Input.actionFile.action_sets[actionSetIndex];

            string shortName = GetValidIdentifier(actionSet.shortName);

            string codeFriendlyInstanceName = GetCodeFriendlyInstanceName(shortName);

            Type setType = typeof(SteamVR_Input_ActionSet).Assembly.GetType(GetSetClassName(actionSet));

            CodeMemberField actionSetInstance = CreateInstanceField(inputClass, shortName, setType);

            CodeMemberProperty actionStaticProperty = CreateStaticProperty(inputClass, shortName, setType, codeFriendlyInstanceName);

            actionSetsArray.Initializers.Add(new CodeFieldReferenceExpression(new CodeThisReferenceExpression(), codeFriendlyInstanceName));


            //add an invoke to the init method
            CodeMethodInvokeExpression initializeActionMethod = AddStaticInvokeToMethod(initializeMethod, shortName, initializeActionSetMethodInfo.Name);
        }

        AddAssignStatement(actionSetsInitMethod, GetCodeFriendlyInstanceName(SteamVR_Input_Generator_Names.actionSetsFieldName), actionSetsArray);

        initializeMethod.Statements.Add(new CodeMethodInvokeExpression(steamVRInputInstance, actionSetsInitMethod.Name));

        // Build the output file name.
        string fullSourceFilePath = GetSourceFilePath(actionSetsClassFileName);
        CreateFile(fullSourceFilePath, compileUnit);
    }

    private static CSharpCodeProvider provider = new CSharpCodeProvider();
    private static string GetValidIdentifier(string name)
    {
        string newName = name.Replace("-", "_");
        newName = provider.CreateValidIdentifier(newName);
        return newName;
    }

    public static MethodInfo GetMethodInfo<T>(Expression<Action<T>> expression)
    {
        var member = expression.Body as MethodCallExpression;

        if (member != null)
            return member.Method;

        throw new ArgumentException("Expression is not a method", "expression");
    }

    private static string GetCodeFriendlyInstanceName(string action)
    {
        return instancePrefix + action;
    }

    private static CodeTypeDeclaration CreatePartialInputClass(CodeCompileUnit compileUnit)
    {
        CodeNamespace codeNamespace = new CodeNamespace();
        codeNamespace.Imports.Add(new CodeNamespaceImport("System"));
        codeNamespace.Imports.Add(new CodeNamespaceImport("UnityEngine"));
        compileUnit.Namespaces.Add(codeNamespace);

        CodeTypeDeclaration inputClass = new CodeTypeDeclaration(typeof(SteamVR_Input).Name);
        inputClass.BaseTypes.Add(typeof(MonoBehaviour));
        inputClass.IsPartial = true;
        codeNamespace.Types.Add(inputClass);

        return inputClass;
    }

    private static string GetActionListClassName(SteamVR_Input_ActionFile_ActionSet set, SteamVR_Input_ActionDirections direction)
    {
        return actionSetClassNamePrefix + set.shortName + "_" + direction.ToString();
    }

    private static CodeTypeDeclaration CreateActionListClass(SteamVR_Input_ActionFile_ActionSet set, SteamVR_Input_ActionDirections direction)
    {
        CodeCompileUnit compileUnit = new CodeCompileUnit();

        CodeNamespace codeNamespace = new CodeNamespace();
        codeNamespace.Imports.Add(new CodeNamespaceImport("System"));
        codeNamespace.Imports.Add(new CodeNamespaceImport("UnityEngine"));
        compileUnit.Namespaces.Add(codeNamespace);

        CodeTypeDeclaration setClass = new CodeTypeDeclaration(GetActionListClassName(set, direction));
        setClass.BaseTypes.Add(typeof(SteamVR_Input_Action_List));
        setClass.Attributes = MemberAttributes.Public;
        codeNamespace.Types.Add(setClass);

        if (direction == SteamVR_Input_ActionDirections.In)
        {
            foreach (var action in set.actionsInList)
            {
                CreateInstanceField(setClass, action.shortName, GetTypeForAction(action), false);
            }
        }
        else if (direction == SteamVR_Input_ActionDirections.Out)
        {
            foreach (var action in set.actionsOutList)
            {
                CreateInstanceField(setClass, action.shortName, GetTypeForAction(action), false);
            }
        }

        // Build the output file name.
        string folderPath = GetSubFolderPath();
        string fullSourceFilePath = Path.Combine(folderPath, setClass.Name + ".cs");
        CreateFile(fullSourceFilePath, compileUnit);

        return setClass;
    }

    private static string GetSetClassName(SteamVR_Input_ActionFile_ActionSet set)
    {
        return actionSetClassNamePrefix + set.shortName;
    }

    private const string inActionsFieldName = "inActions"; //in
    private const string outActionsFieldName = "outActions"; //out
    private static CodeTypeDeclaration CreateActionSetClass(SteamVR_Input_ActionFile_ActionSet set, CodeTypeDeclaration inClass, CodeTypeDeclaration outClass)
    {
        CodeCompileUnit compileUnit = new CodeCompileUnit();

        CodeNamespace codeNamespace = new CodeNamespace();
        codeNamespace.Imports.Add(new CodeNamespaceImport("System"));
        codeNamespace.Imports.Add(new CodeNamespaceImport("UnityEngine"));
        compileUnit.Namespaces.Add(codeNamespace);

        CodeTypeDeclaration setClass = new CodeTypeDeclaration(GetSetClassName(set));
        setClass.BaseTypes.Add(typeof(SteamVR_Input_ActionSet));
        setClass.Attributes = MemberAttributes.Public;
        codeNamespace.Types.Add(setClass);

        if (inClass != null)
        {
            CreateInstanceField(setClass, inActionsFieldName, inClass.Name, false);
        }

        if (outClass != null)
        {
            CreateInstanceField(setClass, outActionsFieldName, outClass.Name, false);
        }

        // Build the output file name.
        string folderPath = GetSubFolderPath();
        string fullSourceFilePath = Path.Combine(folderPath, setClass.Name + ".cs");
        CreateFile(fullSourceFilePath, compileUnit);

        return setClass;
    }

    private static CodeMemberMethod CreateMethod(CodeTypeDeclaration inputClass, string methodName, bool isStatic)
    {
        CodeMemberMethod method = new CodeMemberMethod();
        method.Name = methodName;
        method.Attributes = MemberAttributes.Public;

        if (isStatic)
            method.Attributes = method.Attributes | MemberAttributes.Static;

        inputClass.Members.Add(method);
        return method;
    }

    private static CodeMemberField CreateInstanceField(CodeTypeDeclaration inputClass, string fieldName, Type fieldType, bool addPrefix = true)
    {
        if (addPrefix)
            fieldName = instancePrefix + fieldName;

        if (fieldType == null)
            Debug.Log("null fieldType");

        CodeMemberField field = new CodeMemberField();
        field.Name = fieldName;
        field.Type = new CodeTypeReference(fieldType);
        field.Attributes = MemberAttributes.Public;
        inputClass.Members.Add(field);

        return field;
    }

    private static CodeMemberField CreateInstanceField(CodeTypeDeclaration inputClass, string fieldName, string fieldType, bool addPrefix = true)
    {
        if (addPrefix)
            fieldName = instancePrefix + fieldName;

        CodeMemberField field = new CodeMemberField();
        field.Name = fieldName;
        field.Type = new CodeTypeReference(fieldType);
        field.Attributes = MemberAttributes.Public;
        inputClass.Members.Add(field);

        return field;
    }

    private static CodeFieldReferenceExpression steamVRInputInstance;
    private static CodeMemberProperty CreateStaticProperty(CodeTypeDeclaration inputClass, string propertyName, Type propertyType, string instanceFieldName = null)
    {
        if (instanceFieldName == null)
            instanceFieldName = instancePrefix + propertyName;

        //add the static property
        CodeMemberProperty property = new CodeMemberProperty();
        property.Name = propertyName;
        property.Type = new CodeTypeReference(propertyType);
        property.Attributes = MemberAttributes.Public | MemberAttributes.Static;
        property.HasGet = true;
        property.GetStatements.Add(new CodeMethodReturnStatement(new CodeFieldReferenceExpression(steamVRInputInstance, instanceFieldName)));
        inputClass.Members.Add(property);

        return property;
    }

    private static CodeMemberProperty CreateStaticProperty(CodeTypeDeclaration inputClass, string propertyName, string propertyType, string instanceFieldName = null)
    {
        if (instanceFieldName == null)
            instanceFieldName = instancePrefix + propertyName;

        //add the static property
        CodeMemberProperty property = new CodeMemberProperty();
        property.Name = propertyName;
        property.Type = new CodeTypeReference(propertyType);
        property.Attributes = MemberAttributes.Public | MemberAttributes.Static;
        property.HasGet = true;
        property.GetStatements.Add(new CodeMethodReturnStatement(new CodeFieldReferenceExpression(steamVRInputInstance, instanceFieldName)));
        inputClass.Members.Add(property);

        return property;
    }

    private static CodeMethodInvokeExpression AddStaticInvokeToMethod(CodeMemberMethod methodToAddTo, string staticActionName, string invokeMethodName, string paramName = null)
    {
        CodeMethodInvokeExpression invokeMethod = new CodeMethodInvokeExpression(new CodeMethodReferenceExpression(
            new CodeFieldReferenceExpression(new CodeTypeReferenceExpression(typeof(SteamVR_Input).Name), staticActionName), invokeMethodName));

        if (paramName != null)
            invokeMethod.Parameters.Add(new CodeVariableReferenceExpression(skipStateUpdatesParamName));

        methodToAddTo.Statements.Add(invokeMethod);

        return invokeMethod;
    }

    private static CodeMethodInvokeExpression AddInstanceInvokeToMethod(CodeMemberMethod methodToAddTo, string instanceActionName, string invokeMethodName, string paramName = null)
    {
        CodeMethodInvokeExpression invokeMethod = new CodeMethodInvokeExpression(new CodeMethodReferenceExpression(
            new CodeFieldReferenceExpression(steamVRInputInstance, instanceActionName), invokeMethodName));

        if (paramName != null)
            invokeMethod.Parameters.Add(new CodeVariableReferenceExpression(skipStateUpdatesParamName));

        methodToAddTo.Statements.Add(invokeMethod);

        return invokeMethod;
    }

    private static void AddAssignStatement(CodeMemberMethod methodToAddTo, string fieldToAssign, CodeArrayCreateExpression array)
    {
        methodToAddTo.Statements.Add(new CodeAssignStatement(new CodeFieldReferenceExpression(new CodeThisReferenceExpression(), fieldToAssign), array));
    }

    private static CodeConditionStatement CreateStringCompareStatement(CodeMemberMethod methodToAddTo, string action, string paramName, string returnActionName)
    {
        MethodInfo stringEqualsMethodInfo = GetMethodInfo<string>(set => string.Equals(null, null, StringComparison.CurrentCultureIgnoreCase));
        CodeTypeReferenceExpression stringType = new CodeTypeReferenceExpression(typeof(string));
        CodePrimitiveExpression actionName = new CodePrimitiveExpression(action);
        CodeVariableReferenceExpression pathName = new CodeVariableReferenceExpression(paramName);
        CodeVariableReferenceExpression caseInvariantName = new CodeVariableReferenceExpression("StringComparison.CurrentCultureIgnoreCase");
        CodeMethodInvokeExpression stringCompare = new CodeMethodInvokeExpression(stringType, stringEqualsMethodInfo.Name, pathName, actionName, caseInvariantName);
        CodeMethodReturnStatement returnAction = new CodeMethodReturnStatement(new CodeFieldReferenceExpression(steamVRInputInstance, returnActionName));

        CodeConditionStatement condition = new CodeConditionStatement(stringCompare, returnAction);
        methodToAddTo.Statements.Add(condition);

        return condition;
    }
}