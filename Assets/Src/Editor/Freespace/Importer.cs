using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using System.IO;
using Freespace.POFModel;
using Freespace.POFModel.Geometry;
using Src;
using UnityEngine;

namespace Freespace {

    public class Importer {

        [MenuItem("Assets/Import Freespace")]
        public static void ImportFreespace() {
            string path = EditorUtility.OpenFilePanel("POF file", "", "pof");

            if (string.IsNullOrEmpty(path)) return;

            Importer importer = new Importer(path);

            try {
                importer.Import();
            }
            finally {
                EditorUtility.ClearProgressBar();
            }
        }

        [MenuItem("Assets/Mass Import Freespace")]
        public static void MassImportFreespace() {
            string path = EditorUtility.OpenFolderPanel("POF Directory", "", "pof");
            if (string.IsNullOrEmpty(path)) return;

            string[] paths = Directory.GetFiles(path, "*.pof", SearchOption.AllDirectories);

            try {
                for (int i = 0; i < paths.Length; i++) {
                    Importer importer = new Importer(paths[i], "Importing ");
                    importer.Import();
                }
            }
            finally {
                EditorUtility.ClearProgressBar();
            }
        }

        private readonly Header header;
        private readonly ShieldData shieldData;
        private string[] textureList;
        private readonly List<SubObject> subObjects;
        private readonly List<SpecialPoint> specialPoints;
        private readonly List<GunSlot> gunSlots;
        private readonly List<MissileSlot> missileSlots;
        private readonly List<TurretInfo> turrets;
        private readonly List<DockPoint> dockPoints;
        private readonly List<Thruster> thrusters;
        private readonly List<PathInfo> aiPaths;
        private readonly List<HullLight> hullLights;
        private readonly List<EyePosition> eyePositions;

        private readonly HashSet<SubObject> processedSubObjects;
        private readonly Dictionary<int, Material> materialMap;

        private readonly string progressTitle;
        private readonly ByteBufferReader reader;
        private readonly string originalFilePath;
        private readonly string modelName;
        private readonly string texturePath;
        private readonly string materialPath;
        private readonly string meshPath;
        private readonly string AssetRootPath;

        public Importer(string originalFilePath, string progressTitle = "Importing") {
            this.originalFilePath = originalFilePath;
            this.progressTitle = progressTitle;
            modelName = ImportUtil.CultureInfo.TextInfo.ToTitleCase(Path.GetFileNameWithoutExtension(originalFilePath));
            string importPath = GetImportPath();
            texturePath = Path.Combine(importPath, "Textures");
            materialPath = Path.Combine(importPath, "Materials");
            meshPath = Path.Combine(importPath, "Meshes");
            AssetRootPath = "Assets/Freespace Assets/" + ImportUtil.GetShipClassFromPath(originalFilePath) + "/" +
                            modelName;
            materialMap = new Dictionary<int, Material>();
            processedSubObjects = new HashSet<SubObject>();
            byte[] bytes = File.ReadAllBytes(originalFilePath);
            reader = new ByteBufferReader(bytes);
            header = new Header();
            shieldData = new ShieldData();
            subObjects = new List<SubObject>();
            specialPoints = new List<SpecialPoint>();
            gunSlots = new List<GunSlot>();
            missileSlots = new List<MissileSlot>();
            turrets = new List<TurretInfo>();
            dockPoints = new List<DockPoint>();
            thrusters = new List<Thruster>();
            aiPaths = new List<PathInfo>();
            hullLights = new List<HullLight>();
            eyePositions = new List<EyePosition>();
        }

        private bool Import() {
            GameObject existingPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GetPrefabPath());
            bool overwrite = true;

            if (existingPrefab != null) {
                overwrite = EditorUtility.DisplayDialog("Prefab already exists",
                    "Overwrite? This will break any existing prefabs", "OK");
            }

            if (!overwrite) return false;

            ShowProgress("Parsing POF file", 0);

            if (!ParsePOF()) return false;

            ImportTextures();
            CreateMaterialAssets();
            CreateMeshAssets();
            CreatePrefabs(existingPrefab);

            return true;
        }

        private bool ParsePOF() {
            string signature = reader.ReadString(4);
            int version = reader.ReadInt();

            if (signature != "PSPO") {
                Debug.LogError("Invalid POF file signature: " + signature);
                return false;
            }

            if (version < 2117) {
                Debug.LogError("Invalid POF file version: " + version);
                return false;
            }

            while (!reader.ReachedEOF) {
                string blockType = reader.ReadString(4);
                int blockSize = reader.ReadInt();
                int startPtr = reader.GetPtr();

                switch (blockType) {
                    case "TXTR":
                        ParseTextureSection();
                        break;
                    case "HDR2":
                        ParseHeaderSection();
                        break;
                    case "OBJ2":
                        ParseSubObjectSection();
                        break;
                    case "SPCL":
                        ParseSpecialPointSection();
                        break;
                    case "GPNT":
                        ParseGunPointSection();
                        break;
                    case "MPNT":
                        ParseMissilePointSection();
                        break;
                    case "TGUN":
                        ParseTurretGunSection();
                        break;
                    case "TMIS":
                        ParseTurretMissileSection();
                        break;
                    case "DOCK":
                        ParseDockPointSection();
                        break;
                    case "FUEL":
                        ParseFuelSection();
                        break;
                    case "SHLD":
                        ParseShieldSection();
                        break;
                    case "EYE ":
                        ParseEyeSection();
                        break;
                    case "ACEN":
                        ParseAutoCenterSection();
                        break;
                    case "INSG":
                        ParseInsigniaSection();
                        break;
                    case "PATH":
                        ParsePathSection();
                        break;
                    case "GLOW":
                        ParseGlowSection();
                        break;
                    case "SLDC":
                        ParseShieldCollisionBSPSection();
                        break;
                    case "PINF":
                        ParsePOFInfoSection(blockSize);
                        break;

                    default:
                        Debug.LogError("UNKNOWN BLOCK TYPE " + blockType);
                        return false;
                }

                AssertSectionFullyRead(blockType, startPtr, blockSize);
            }

            return true;
        }

        private void ImportTextures() {
            ShowProgress("Copying Textures", 0.25f);
            string textureImportRoot = ImportUtil.GetTexturePathFromImportLocation(originalFilePath);

            Directory.CreateDirectory(texturePath);

            string[] extensions = {"", "-glow", "-shine"};
            // if we dont have the texture, and it does not already exist, copy it in

            for (int i = 0; i < textureList.Length; i++) {
                for (int j = 0; j < extensions.Length; j++) {
                    string[] pathSegments = {
                        textureImportRoot, textureList[i] + extensions[j], ".dds"
                    };
                    string pathToTexture = ImportUtil.MakePath(pathSegments);

                    if (File.Exists(pathToTexture)) {
                        File.Copy(pathToTexture, GetTextureAssetPath(pathToTexture), true);
                    }
                }
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        private void CreateMaterialAssets() {
            ShowProgress("Creating Materials", 0.25f);

            Shader standardShader = Shader.Find("Standard");

            Directory.CreateDirectory(materialPath);

            for (int i = 0; i < textureList.Length; i++) {
                string materialString = "(" + i + "/" + textureList.Length + ")";
                float progress = (i / (float) textureList.Length);
                ShowProgress("Creating Materials " + materialString, progress);

                string textureName = textureList[i];
                Texture2D diffuse = ImportUtil.GetTexture(AssetRootPath, textureName, string.Empty);
                Texture2D shine = ImportUtil.GetTexture(AssetRootPath, textureName, "-shine");
                Texture2D glow = ImportUtil.GetTexture(AssetRootPath, textureName, "-glow");
                Material material = new Material(standardShader);

                material.SetColor("_EmissionColor", Color.white);
                material.SetTexture("_MainTex", diffuse);
                material.SetTexture("_EmissionMap", glow);
                material.SetTexture("_MetallicGlossMap", shine);
                materialMap.Add(i, material);
                AssetDatabase.CreateAsset(material, GetMaterialPath(textureName));
            }
        }

        private void CreateMeshAssets() {
            Directory.CreateDirectory(meshPath);

            for (int i = 0; i < subObjects.Count; i++) {
                float progress = (i / (float) subObjects.Count);
                string meshCount = "(" + (i + 1) + "/" + subObjects.Count + ")";
                ShowProgress("Creating mesh " + meshCount, progress);

                KeyValuePair<Mesh, int[]> pair = new GeometryParser(subObjects[i].bspData).GetMeshAndTextureIndices();
                subObjects[i].mesh = pair.Key;
                subObjects[i].textureIndices = pair.Value;
                
                AssetDatabase.CreateAsset(subObjects[i].mesh, GetMeshPath(subObjects[i].submodelName));
            }
            AssetDatabase.SaveAssets();
        }

        private void ShowProgress(string description, float progress) {
            EditorUtility.DisplayProgressBar(progressTitle, description, progress);
        }

        private string GetImportPath() {
            string rootPath = Path.Combine(Application.dataPath, "Freespace Assets");
            string classPath = Path.Combine(rootPath, ImportUtil.GetShipClassFromPath(originalFilePath));
            return Path.Combine(classPath, modelName);
        }

        private string GetTextureAssetPath(string textureName) {
            return Path.Combine(texturePath, Path.GetFileName(textureName));
        }

        private string GetPrefabPath() {
            return AssetRootPath + "/" + modelName + ".prefab";
        }

        private string GetMeshPath(string meshName) {
            return AssetRootPath + "/Meshes/" + meshName + ".asset";
        }

        private string GetMaterialPath(string materialName) {
            return AssetRootPath + "/Materials/" + materialName + ".mat";
        }

        private void ParseTextureSection() {
            int textureCount = reader.ReadInt();
            textureList = new string[textureCount];

            for (int i = 0; i < textureCount; i++) {
                textureList[i] = reader.ReadString();
            }
        }

        private void ParseHeaderSection() {
            header.maxRadius = reader.ReadFloat();
            header.objectFlags = reader.ReadInt();
            header.subObjectCount = reader.ReadInt();
            header.minBounding = reader.ReadVector3();
            header.maxBounding = reader.ReadVector3();
            int detailLevels = reader.ReadInt();

            header.detailLevelIndices = new int[detailLevels];

            for (int i = 0; i < detailLevels; i++) {
                header.detailLevelIndices[i] = reader.ReadInt();
            }

            int debrisCount = reader.ReadInt();
            header.debrisCountIndices = new int[debrisCount];

            for (int i = 0; i < debrisCount; i++) {
                header.debrisCountIndices[i] = reader.ReadInt();
            }

            header.mass = reader.ReadFloat();
            header.centerOfMass = reader.ReadVector3();
            header.momentOfInertia = reader.ReadFloatArray(9);
            int crossSectionCount = reader.ReadInt();
            if (crossSectionCount < 0) crossSectionCount = 0;
            header.crossSections = new CrossSection[crossSectionCount];

            for (int i = 0; i < header.crossSections.Length; i++) {
                header.crossSections[i].depth = reader.ReadFloat();
                header.crossSections[i].radius = reader.ReadFloat();
            }

            header.muzzleLights = new MuzzleLight[reader.ReadInt()];

            for (int i = 0; i < header.muzzleLights.Length; i++) {
                header.muzzleLights[i].location = reader.ReadVector3();
                header.muzzleLights[i].lightType = reader.ReadInt();
            }
        }

        private void ParseSubObjectSection() {
            SubObject subObject = new SubObject();
            subObject.subModelNumber = reader.ReadInt();
            subObject.radius = reader.ReadFloat();
            subObject.submodelParent = reader.ReadInt();
            subObject.offset = reader.ReadVector3();
            subObject.geometricCenter = reader.ReadVector3();
            subObject.boundingBoxMin = reader.ReadVector3();
            subObject.boundingBoxMax = reader.ReadVector3();
            subObject.submodelName = reader.ReadString();
            subObject.properties = reader.ReadString();
            subObject.movementType = reader.ReadInt();
            subObject.movementAxis = reader.ReadInt();
            subObject.reserved = reader.ReadInt();
            subObject.bspData = reader.ReadByteArray(reader.ReadInt());
            subObject.reserved = 0;

            if (subObject.submodelName == string.Empty) {
                subObject.submodelName = "SubObject " + subObject.subModelNumber;
            }
            
            subObjects.Add(subObject);
        }

        private void ParseSpecialPointSection() {
            int specialPointCount = reader.ReadInt();

            for (int i = 0; i < specialPointCount; i++) {
                SpecialPoint specialPoint = new SpecialPoint();
                specialPoint.name = reader.ReadString();
                specialPoint.properties = reader.ReadString();
                specialPoint.point = reader.ReadVector3();
                specialPoint.radius = reader.ReadFloat();
                specialPoints.Add(specialPoint);
            }
        }

        // A "slot" is what you see in the loadout screen. Primaries have a max of 2
        // and secondaries of 3 for player-flyable ships.
        // "Guns" are the actual number of barrels and hence projectiles you'll get when you press the trigger.
        // There is likely no practical max.
        private void ParseGunPointSection() {
            int slots = reader.ReadInt();

            for (int i = 0; i < slots; i++) {
                GunSlot slot = new GunSlot();
                gunSlots.Add(slot);
                slot.gunPoints = new PositionNormal[reader.ReadInt()];

                for (int j = 0; j < slot.gunPoints.Length; j++) {
                    slot.gunPoints[j] = new PositionNormal(reader.ReadVector3(), reader.ReadVector3());
                }
            }
        }

        private void ParseMissilePointSection() {
            int slots = reader.ReadInt();

            for (int i = 0; i < slots; i++) {
                MissileSlot slot = new MissileSlot();
                missileSlots.Add(slot);
                slot.missilePoints = new PositionNormal[reader.ReadInt()];

                for (int j = 0; j < slot.missilePoints.Length; j++) {
                    slot.missilePoints[j] = new PositionNormal(reader.ReadVector3(), reader.ReadVector3());
                }
            }
        }

        private void ParseTurretGunSection() {
            int bankCount = reader.ReadInt();

            for (int i = 0; i < bankCount; i++) {
                TurretInfo turret = new TurretInfo(TurretType.Gun);
                turret.parentSubObjectIndex = reader.ReadInt();
                turret.rotationBaseSubObjectIndex = reader.ReadInt();
                turret.turretNormal = reader.ReadVector3();
                turret.firingPoints = reader.ReadVector3Array(reader.ReadInt());
                turrets.Add(turret);
            }
        }

        private void ParseTurretMissileSection() {
            int bankCount = reader.ReadInt();

            for (int i = 0; i < bankCount; i++) {
                TurretInfo turret = new TurretInfo(TurretType.Missile);
                turret.parentSubObjectIndex = reader.ReadInt();
                turret.rotationBaseSubObjectIndex = reader.ReadInt();
                turret.turretNormal = reader.ReadVector3();
                turret.firingPoints = reader.ReadVector3Array(reader.ReadInt());
                turrets.Add(turret);
            }
        }

        // Note: Properties… if $name= found, then this is name.
        // If name is cargo then this is a cargo bay.
        private void ParseDockPointSection() {
            int dockPointCount = reader.ReadInt();

            for (int i = 0; i < dockPointCount; i++) {
                DockPoint dockPoint = new DockPoint();
                dockPoint.properties = reader.ReadString();
                dockPoint.pathNumber = reader.ReadIntArray(reader.ReadInt());

                int pointCount = reader.ReadInt();
                dockPoint.points = new PositionNormal[pointCount];

                for (int j = 0; j < pointCount; j++) {
                    dockPoint.points[j].point = reader.ReadVector3();
                    dockPoint.points[j].normal = reader.ReadVector3();
                }

                dockPoints.Add(dockPoint);
            }
        }

        private void ParseFuelSection() {
            int thrusterCount = reader.ReadInt();

            for (int i = 0; i < thrusterCount; i++) {
                Thruster thruster = new Thruster();
                thruster.glows = new ThrusterGlow[reader.ReadInt()];
                thruster.properties = reader.ReadString();
                thrusters.Add(thruster);

                for (int j = 0; j < thruster.glows.Length; j++) {
                    thruster.glows[j].position = reader.ReadVector3();
                    thruster.glows[j].normal = reader.ReadVector3();
                    thruster.glows[j].radius = reader.ReadFloat();
                }
            }
        }

        private void ParseShieldSection() {
            shieldData.vertices = reader.ReadVector3Array(reader.ReadInt());
            shieldData.faces = new Face[reader.ReadInt()];

            for (int i = 0; i < shieldData.faces.Length; i++) {
                Face face = new Face();
                face.normal = reader.ReadVector3();
                face.vertexIndices = reader.ReadIntArray(3);
                face.neighborIndices = reader.ReadIntArray(3);
                shieldData.faces[i] = face;
            }
        }

        private void ParseEyeSection() {
            int eyePositionCount = reader.ReadInt();

            for (int i = 0; i < eyePositionCount; i++) {
                EyePosition eyePosition = new EyePosition();
                eyePosition.parentSubObjectIndex = reader.ReadInt();
                eyePosition.position = reader.ReadVector3();
                eyePosition.normal = reader.ReadVector3();
                eyePositions.Add(eyePosition);
            }
        }

        private void ParseAutoCenterSection() {
            reader.ReadVector3();
        }

        private void ParseInsigniaSection() {
           
            int insigniaCount = reader.ReadInt();

            for (int i = 0; i < insigniaCount; i++) {
                Insignia insignia = new Insignia();
                insignia.detailLevel = reader.ReadInt();
                insignia.faces = new InsigniaFace[reader.ReadInt()];
                insignia.vertices = new Vector3[reader.ReadInt()];

                for (int j = 0; j < insignia.vertices.Length; j++) {
                    insignia.vertices[j] = reader.ReadVector3();
                }

                insignia.offset = reader.ReadVector3();

                for (int j = 0; j < insignia.faces.Length; j++) {
                    InsigniaFace face = new InsigniaFace();
                    face.vertexIndex0 = reader.ReadInt();
                    face.u0 = reader.ReadFloat();
                    face.v0 = reader.ReadFloat();
                    face.vertexIndex1 = reader.ReadInt();
                    face.u1 = reader.ReadFloat();
                    face.v1 = reader.ReadFloat();
                    face.vertexIndex2 = reader.ReadInt();
                    face.u2 = reader.ReadFloat();
                    face.v2 = reader.ReadFloat();
                    insignia.faces[j] = face;
                }
            }
        }

        private void ParsePathSection() {
            int pathCount = reader.ReadInt();

            for (int i = 0; i < pathCount; i++) {
                PathInfo pathInfo = new PathInfo();
                pathInfo.name = reader.ReadString();
                pathInfo.parentName = reader.ReadString();
                PathVertex[] points = new PathVertex[reader.ReadInt()];

                for (int j = 0; j < points.Length; j++) {
                    PathVertex point = new PathVertex();
                    point.position = reader.ReadVector3();
                    point.radius = reader.ReadFloat();
                    point.subObjectIndices = reader.ReadIntArray(reader.ReadInt());
                    points[j] = point;
                }

                pathInfo.points = points;
                aiPaths.Add(pathInfo);
            }
        }

        private void ParseGlowSection() {
            int glowBankCount = reader.ReadInt();

            for (int i = 0; i < glowBankCount; i++) {
                HullLight light = new HullLight();
                light.displayTime = reader.ReadInt();
                light.onTime = reader.ReadInt();
                light.offTime = reader.ReadInt();
                light.parentIndex = reader.ReadInt();
                light.lod = reader.ReadInt();
                light.type = reader.ReadInt();
                light.lights = new HullLightPoint[reader.ReadInt()];
                light.properties = reader.ReadString();

                for (int j = 0; j < light.lights.Length; j++) {
                    light.lights[j].point = reader.ReadVector3();
                    light.lights[j].normal = reader.ReadVector3();
                    light.lights[j].radius = reader.ReadFloat();
                }

                hullLights.Add(light);
            }
        }

        private void ParseShieldCollisionBSPSection() {
            shieldData.collisionBSP = reader.ReadByteArray(reader.ReadInt());
        }

        private void ParsePOFInfoSection(int size) {
            reader.ReadString(size);
        }

        private void AssertSectionFullyRead(string sectionName, int startPtr, int size) {
            if (reader.GetPtr() - startPtr != size) {
                Debug.Log("Failed to fully read section: " + sectionName);
            }
        }

        private SubObject GetSubObjectByIndex(int index) {
            if (index < 0 || index >= subObjects.Count) {
                return null;
            }

            return subObjects[index];
        }

        private GameObject CreateTurretAssets() {
            if (turrets.Count == 0) return null;
            GameObject turretRoot = new GameObject("Turrets");

            for (int i = 0; i < turrets.Count; i++) {
                TurretInfo turret = turrets[i];
                SubObject obj = subObjects[turret.parentSubObjectIndex];
                GameObject turretObj = CreateRenderableGameObject(obj);

                if (turret.type == TurretType.Gun) {
                    turretObj.name = "[gun] " + turretObj.name;
                }
                else {
                    turretObj.name = "[missile] " + turretObj.name;
                }

                BoxCollider collider = turretObj.AddComponent<BoxCollider>();
                collider.size = obj.boundingBoxMax - obj.boundingBoxMin;
                turretObj.transform.parent = turretRoot.transform;
                turretObj.transform.localPosition = obj.offset;
                Turret turretComponent = turretObj.AddComponent<Turret>();
                turretComponent.normal = turret.turretNormal;
                turretComponent.firingPoints = turret.firingPoints;

                List<SubObject> children = GetChildSubObjects(obj);

                for (int j = 0; j < children.Count; j++) {
                    SubObject child = children[j];
                    GameObject turretChild = CreateRenderableGameObject(child);
                    turretChild.transform.parent = turretObj.transform;
                    turretChild.transform.localPosition = new Vector3(); // not offset for some reason 
                }
            }

            return turretRoot;
        }

        private static Mesh EnsureMeshIsValid(SubObject obj) {
            if (obj.mesh != null) return obj.mesh;
            GeometryParser parser = new GeometryParser(obj.bspData);
            obj.mesh = parser.GetMeshAndTextureIndices().Key;
            return obj.mesh;
        }
        
        private GameObject CreateRenderableGameObject(SubObject obj) {
            GameObject root = new GameObject(obj.submodelName);
            MeshFilter meshFilter = root.AddComponent<MeshFilter>();
            meshFilter.mesh = AssetDatabase.LoadAssetAtPath<Mesh>(GetMeshPath(obj.submodelName));
            Renderer renderer = root.AddComponent<MeshRenderer>();
            Material[] materials = new Material[obj.textureIndices.Length];
            processedSubObjects.Add(obj);

            for (int i = 0; i < obj.textureIndices.Length; i++) {
                Material material;

                if (materialMap.TryGetValue(obj.textureIndices[i], out material)) {
                    int textureIndex = obj.textureIndices[i];
                    string materialName = textureList[textureIndex];
                    materials[i] = AssetDatabase.LoadAssetAtPath<Material>(GetMaterialPath(materialName));
                }
            }

            renderer.sharedMaterials = materials;
            return root;
        }

        private void CreatePrefabs(GameObject existingPrefab) {
            ShowProgress("Creating Prefabs", 0.9f);

            GameObject assetRoot = new GameObject(modelName);

            GameObject turretRoot = CreateTurretAssets();
            GameObject models = CreateLODModels();
            GameObject debris = CreateDebrisModels();
            GameObject shield = CreateShieldModel();
            GameObject thrusterRoot = CreateThrusters();
            GameObject gunpointRoot = CreateGunPoints();
            GameObject missilePointRoot = CreateMissilePoints();
            GameObject extras = CreateExtras();

            if (models != null) models.transform.parent = assetRoot.transform;
            if (turretRoot != null) turretRoot.transform.parent = assetRoot.transform;
            if (debris != null) debris.transform.parent = assetRoot.transform;
            if (shield != null) shield.transform.parent = assetRoot.transform;
            if (thrusterRoot != null) thrusterRoot.transform.parent = assetRoot.transform;
            if (gunpointRoot != null) gunpointRoot.transform.parent = assetRoot.transform;
            if (missilePointRoot != null) missilePointRoot.transform.parent = assetRoot.transform;
            if (extras != null) extras.transform.parent = assetRoot.transform;

            if (existingPrefab) {
                PrefabUtility.ReplacePrefab(assetRoot, existingPrefab);
            }
            else {
                PrefabUtility.CreatePrefab(GetPrefabPath(), assetRoot);
            }

            UnityEngine.Object.DestroyImmediate(assetRoot);
            EditorUtility.ClearProgressBar();
        }

        private GameObject CreateLODModels() {
            GameObject retn = new GameObject("Detail Root");
            BoxCollider collider = retn.AddComponent<BoxCollider>();
            collider.size = header.maxBounding - header.minBounding;
            LODGroup lodGroup = retn.AddComponent<LODGroup>();
            LOD[] lods = new LOD[header.detailLevelIndices.Length];

            for (int i = 0; i < header.detailLevelIndices.Length; i++) {
                int subModelIdx = header.detailLevelIndices[i];
                SubObject subObject = GetSubObjectByIndex(subModelIdx);
                GameObject go = CreateRenderableGameObject(subObject);
                go.transform.parent = retn.transform;
                lods[i] = new LOD(1f / (i + 1), go.GetComponents<Renderer>());
            }

            lodGroup.SetLODs(lods);
            lodGroup.RecalculateBounds();

            return retn;
        }

        private GameObject CreateDebrisModels() {
            if (header.debrisCountIndices.Length == 0) {
                return null;
            }

            GameObject retn = new GameObject("Debris Root");

            for (int i = 0; i < header.debrisCountIndices.Length; i++) {
                int subModelIdx = header.debrisCountIndices[i];
                SubObject subObject = GetSubObjectByIndex(subModelIdx);
                GameObject go = CreateRenderableGameObject(subObject);
                BoxCollider collider = go.AddComponent<BoxCollider>();
                collider.size = subObject.boundingBoxMax - subObject.boundingBoxMax;
                go.transform.parent = retn.transform;
                go.transform.localPosition = subObject.offset;
            }

            retn.SetActive(false);
            return retn;
        }

        private GameObject CreateShieldModel() {
            if (shieldData == null || shieldData.faces == null) return null;

            GameObject shieldObj = new GameObject("Shield");
            Mesh mesh = new Mesh();

            int[] triangles = new int[shieldData.faces.Length * 3];
            int triangleCount = 0;

            for (int i = 0; i < shieldData.faces.Length; i++) {
                Face face = shieldData.faces[i];
                triangles[triangleCount++] = face.vertexIndices[0];
                triangles[triangleCount++] = face.vertexIndices[1];
                triangles[triangleCount++] = face.vertexIndices[2];
            }

            mesh.vertices = shieldData.vertices;
            mesh.triangles = triangles;
            AssetDatabase.CreateAsset(mesh, AssetRootPath + "/Meshes/Shield.asset");
            MeshFilter filter = shieldObj.AddComponent<MeshFilter>();
            filter.mesh = mesh;
            MeshRenderer renderer = shieldObj.AddComponent<MeshRenderer>();
            renderer.enabled = false;
            return shieldObj;
        }

        //todo -- this needs to fleshed out more once weapons work.
        //probably dont want an object for each point. Probably want
        //to add a gunslot component here to contain this data instead.
        private GameObject CreateGunPoints() {
            if (gunSlots.Count == 0) return null;
            GameObject retn = new GameObject("Gun Points");

            for (int i = 0; i < gunSlots.Count; i++) {
                GunSlot slot = gunSlots[i];

                for (int j = 0; j < slot.gunPoints.Length; j++) {
                    GameObject go = new GameObject("Gun Point " + i + " -- " + j);
                    PositionNormal gunPoint = slot.gunPoints[j];
                    go.transform.parent = retn.transform;
                    go.transform.position = gunPoint.point;
                    if (gunPoint.normal == Vector3.zero) gunPoint.normal = Vector3.forward;
                    go.transform.rotation = Quaternion.LookRotation(gunPoint.normal, Vector3.up);
                }
            }

            return retn;
        }

        //todo -- this needs to fleshed out more once weapons work.
        //probably dont want an object for each point. Probably want
        //to add a gunslot component here to contain this data instead.
        private GameObject CreateMissilePoints() {
            if (missileSlots.Count == 0) return null;
            GameObject retn = new GameObject("Missile Points");

            for (int i = 0; i < missileSlots.Count; i++) {
                MissileSlot slot = missileSlots[i];

                for (int j = 0; j < slot.missilePoints.Length; j++) {
                    GameObject go = new GameObject("Missile Point " + i + " -- " + j);
                    PositionNormal missilePoint = slot.missilePoints[j];
                    go.transform.parent = retn.transform;
                    go.transform.position = missilePoint.point;
                    if (missilePoint.normal == Vector3.zero) missilePoint.normal = Vector3.forward;
                    go.transform.rotation = Quaternion.LookRotation(missilePoint.normal, Vector3.up);
                }
            }

            return retn;
        }

        //todo -- we are using this information for anything yet
        //todo -- create a ThursterGlow component and use normal/radius from glows for effects
        private GameObject CreateThrusters() {
            if (thrusters.Count == 0) return null;
            GameObject retn = new GameObject("Thrusters");

            for (int i = 0; i < thrusters.Count; i++) {
                Thruster thruster = thrusters[i];
                GameObject thrusterGo = new GameObject("Thruster " + i);
                thrusterGo.transform.parent = retn.transform;

                for (int j = 0; j < thruster.glows.Length; j++) {
                    ThrusterGlow glow = thruster.glows[j];
                    GameObject glowGo = new GameObject("Thruster Glow " + j);
                    glowGo.transform.parent = thrusterGo.transform;
                    glowGo.transform.position = glow.position;
                    glowGo.transform.rotation = Quaternion.LookRotation(glow.normal, Vector3.up);
                    //glowGo.AddComponent<ThrusterGlowPulse>();
                }
            }

            return retn;
        }

        private GameObject CreateExtras() {
            GameObject extras = new GameObject("Extras");

            for (int i = 0; i < subObjects.Count; i++) {
                SubObject subObject = subObjects[i];

                if (processedSubObjects.Contains(subObject)) {
                    continue;
                }

                // ignore pilots for now
                if (subObject.submodelName.Contains("pilot")) {
                    processedSubObjects.Add(subObject);
                    continue;
                }

                // ignore destroyed turrets for now
                if (subObject.submodelName.Contains("-destroyed")) {
                    processedSubObjects.Add(subObject);
                    continue;
                }

                GameObject go = CreateRenderableGameObject(subObject);
                go.transform.parent = extras.transform;
                go.transform.localPosition = subObject.offset;
                if (subObject.properties.Length > 0) Debug.Log(subObject.properties);
            }

            return extras;
        }

        private List<SubObject> GetChildSubObjects(SubObject parent) {
            List<SubObject> retn = new List<SubObject>();

            for (int i = 0; i < subObjects.Count; i++) {
                if (subObjects[i].submodelParent == parent.subModelNumber) {
                    retn.Add(subObjects[i]);
                }
            }

            return retn;
        }

    }

}