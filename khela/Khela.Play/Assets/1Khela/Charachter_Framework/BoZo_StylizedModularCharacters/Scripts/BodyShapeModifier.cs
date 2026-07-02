using UnityEngine;
using System.Collections.Generic;

namespace Bozo.ModularCharacters
{
    public class BodyShapeModifier : MonoBehaviour
    {
        private OutfitSystem system;

        public string shapeName;
        public string sorting;

        public bool useScale;
        public float scaleValue = 1;
        public float xScaleValue = 1;
        public float yScaleValue = 1;
        public float zScaleValue = 1;
        public BodyShapeModifier[] counterScale;
        private Vector3 counterValue;
        public Dictionary<string, Vector3> counterSources = new Dictionary<string, Vector3>();

        public bool useXScale;
        public bool useYScale;
        public bool useZScale;
        public bool linkScaleAxis;

        public Vector2 scaleRange = new Vector2(0.5f, 2f);

        public bool usePosition;
        private Vector3 initalPosition;
        private Vector3 initalMirrorPosition;
        public float posValue = 0;
        public float xPosValue = 0;
        public float yPosValue = 0;
        public float zPosValue = 0;
        public bool useXPos;
        public bool useYPos;
        public bool useZPos;
        public Vector2 posRange = new Vector2(-0.02f, 0.02f);
        [Range(-1,1)]public int mirrorX = 1;
        [Range(-1,1)]public int mirrorY = 1;
        [Range(-1,1)]public int mirrorZ = 1;

        public bool useRotation;
        public Quaternion initalRotationAxis;
        public Quaternion initalMirrorRotationAxis;
        public float rotation;
        public Vector3 rotationAxis;
        public Vector2 rotRange = new Vector2(-90f, 90f);

        [SerializeField] bool MirrorTransform;
        private Transform mirror;

        private bool initalized;
        private bool mute;

        BodyModData initalData;
        BodyModData moddedData;

        private void Awake()
        {
            initalData = GetData();
        }

        private void Start()
        {
            Init();
        }

        public void Init()
        {
            if (initalized) return;
            system = GetComponentInParent<OutfitSystem>();
            initalPosition = transform.localPosition;
            initalRotationAxis = transform.localRotation;

            if (!system)
            {
                return;
            }

            if (MirrorTransform) GetMirror();

            initalized = true;
        }

        public void SetScale(float x, float y, float z, float v) 
        {
            xScaleValue = x;
            yScaleValue = y;
            zScaleValue = z;
            scaleValue = v;
            ApplyScale();
        }

        public void SetScale(float value)
        {
            scaleValue = value;
            ApplyScale();
        }

        public void SetPosition(float x, float y, float z)
        {
            xPosValue = x;
            yPosValue = y;
            zPosValue = z;

            ApplyPosition();
        }

        public void SetRotation(float value)
        {
            rotation = value;
            ApplyRotation();
        }

        public void Apply()
        {
            if (!initalized) Init();

            if (usePosition) ApplyPosition();
            if (useRotation) ApplyRotation();
            if (useScale) ApplyScale();
        }

        public void ApplyScale() 
        {
            scaleValue = Mathf.Clamp(scaleValue, scaleRange.x, scaleRange.y);
            xScaleValue = Mathf.Clamp(xScaleValue, scaleRange.x, scaleRange.y);
            yScaleValue = Mathf.Clamp(yScaleValue, scaleRange.x, scaleRange.y);
            zScaleValue = Mathf.Clamp(zScaleValue, scaleRange.x, scaleRange.y);

            var xSca = 1f;
            var ySca = 1f;
            var zSca = 1f;

            if (linkScaleAxis)
            {
                if (useXScale) xSca = scaleValue;
                if (useYScale) ySca = scaleValue;
                if (useZScale) zSca = scaleValue;
            }
            else
            {
                if (useXScale) xSca = xScaleValue;
                if (useYScale) ySca = yScaleValue;
                if (useZScale) zSca = zScaleValue;
            }

            var scale = new Vector3(xSca, ySca, zSca);
            var counter = new Vector3(1/xSca, 1/ySca, 1/zSca);

            counterValue = Vector3.one;
            foreach (var item in counterSources.Values)
            {
                counterValue = Vector3.Scale(counterValue, item);
            }

            if (!mirror && MirrorTransform) GetMirror();

            transform.localScale = Vector3.Scale(scale, counterValue);
            if (mirror) mirror.localScale = Vector3.Scale(scale, counterValue);

            foreach (var item in counterScale)
            {
                if (!item) continue;
                item.counterSources[this.name] = counter; item.ApplyScale();
            }
        }

        public void ApplyPosition()
        {
            xPosValue = Mathf.Clamp(xPosValue, posRange.x, posRange.y);
            yPosValue = Mathf.Clamp(yPosValue, posRange.x, posRange.y);
            zPosValue = Mathf.Clamp(zPosValue, posRange.x, posRange.y);

            var xPos = 0f;
            var yPos = 0f;
            var zPos = 0f;

            if (useXPos) xPos = xPosValue;
            if (useYPos) yPos = yPosValue;
            if (useZPos) zPos = zPosValue;

            var position = new Vector3(xPos, yPos, zPos);
            var mirrorPosition = new Vector3(xPos * mirrorX, yPos * mirrorY, zPos * mirrorZ);

            if (!mirror && MirrorTransform) GetMirror();

            transform.localPosition = position + initalPosition;
            if (mirror) mirror.localPosition = mirrorPosition + initalMirrorPosition;



        }

        public void ApplyRotation()
        {
            rotation = Mathf.Clamp(rotation, rotRange.x, rotRange.y);

            var rot = new Vector3(rotation * rotationAxis.x, rotation * rotationAxis.y, rotation * rotationAxis.z);


            if (!mirror && MirrorTransform) GetMirror();

            var qRot = initalRotationAxis * Quaternion.Euler(rot);
            var mqRot = initalMirrorRotationAxis * Quaternion.Euler(-rot);

            transform.localRotation = qRot;
            if (mirror)mirror.localRotation = mqRot;
        }

        private void GetMirror()
        {
            if (!system) return;
            var boneName = name;
            boneName = boneName.Replace("_l", "_r");
            var bones = system.GetBones();

            if (bones.TryGetValue(boneName, out Transform bone))
            {
                mirror = bone;
                initalMirrorPosition = mirror.localPosition;
                initalMirrorRotationAxis = mirror.localRotation;
            }
        }

        public BodyModData GetData() 
        {
            var data = new BodyModData();

            data.scaleValue = scaleValue;
            data.scale = new Vector3(xScaleValue, yScaleValue, zScaleValue);

            data.posValue = posValue;
            data.position = new Vector3(xPosValue, yPosValue, zPosValue);

            data.rotation = rotation;
            return data;
        }

        public void SetData(BodyModData data)
        {
            if (data == null) return;

            scaleValue = data.scaleValue;
            xScaleValue = data.scale.x;
            yScaleValue = data.scale.y;
            zScaleValue = data.scale.z;

            posValue = data.posValue;
            xPosValue = data.position.x;
            yPosValue = data.position.y;
            zPosValue = data.position.z;

            rotation = data.rotation;

            Apply();
        }

        public void SetMute(bool mute)
        {
            if (mute == this.mute) return;
            this.mute = mute;
            if (mute)
            {
                moddedData = GetData();
                SetData(initalData);
            }
            else
            {
                SetData(moddedData);
            }
        }

        private void LateUpdate()
        {
            return;
            if (usePosition) ApplyPosition();
            if (useRotation) ApplyRotation();
            if (useScale) ApplyScale();
        }
    }

    [System.Serializable]
    public class BodyModData
    {
        public float scaleValue = 1;
        public Vector3 scale;

        public float posValue = 1;
        public Vector3 position;

        public float rotation;
    }
}

