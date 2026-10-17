
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using DG.Tweening;

public class Stacker : MonoBehaviour
{
    [Header("Objects")]
    [SerializeField] private GameObject prefab;
    [SerializeField] private int objectCount = 11;

    [Header("Pyramid")]
    [SerializeField] private int baseRowCount = 5;
    [SerializeField] private float horizontalSpacing = 1.1f;
    [SerializeField] private float verticalSpacing = 1f;

    [Header("Animation")]
    [SerializeField] private float moveDuration = 0.4f;
    [SerializeField] private Ease moveEase = Ease.OutQuad;
    [SerializeField] private float delayBetweenObjects = 0.02f;

    [Header("Selected Pumpkin")]
    [SerializeField] private Transform SelectedPumpkinPosition;
    [SerializeField] private MeshHoleCutter meshHoleCutter;
    [SerializeField] private CarveScorer carveScorer;


    private readonly List<GameObject> objects = new List<GameObject>();
    public bool CanSelectMore = true;
    private GameObject selectedPumpkin;

    void Start()
    {
        if (meshHoleCutter == null)
            meshHoleCutter = FindObjectOfType<MeshHoleCutter>();

        GeneratePyramid();
    }

    void Update()
    {
        Mouse mouse = Mouse.current;
        Camera cam = Camera.main;

        if (mouse == null || cam == null || !mouse.leftButton.wasPressedThisFrame)
            return;

        Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
        if (Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
        {
            Pumpkin item = hit.collider.GetComponentInParent<Pumpkin>();
            if (item != null)
                item.RemoveFromStack();
        }
    }

    public void GeneratePyramid()
    {
        ClearPyramid();
        CalculateBaseRowCount();

        for (int i = 0; i < objectCount; i++)
        {
            if (prefab == null)
                continue;

            GameObject obj = Instantiate(prefab, transform);
            obj.SetActive(true);
            obj.transform.localPosition = Vector3.zero;
            obj.transform.localRotation = Quaternion.AngleAxis(Random.Range(0f, 360f), Vector3.up);

            Pumpkin item = obj.GetComponent<Pumpkin>();

            if (item == null)
                item = obj.AddComponent<Pumpkin>();

            item.Initialize(this);
            objects.Add(obj);
        }

        RearrangePyramid(false);
    }

    public void RemoveObject(GameObject obj)
    {
        if (!CanSelectMore || !objects.Contains(obj))
            return;

        if (selectedPumpkin == obj)
            return;

        objects.Remove(obj);
        CanSelectMore = false;
        selectedPumpkin = obj;


        obj.transform.DOKill();
        obj.transform.DOMove(SelectedPumpkinPosition.position, moveDuration)
            .SetEase(moveEase).OnComplete(() =>
            {
                if (meshHoleCutter != null)
                {
                    meshHoleCutter.targetPumpkin = obj;
                    meshHoleCutter.SetMode(MeshHoleCutter.Mode.Rotate);
                    meshHoleCutter.canCut = true;
                    meshHoleCutter.SetTargetPumpkin(obj);
                    carveScorer.Initialize();
                }
            });

        obj.transform.DORotate(SelectedPumpkinPosition.rotation.eulerAngles, moveDuration)
            .SetEase(moveEase);

        RearrangePyramid(true);
    }

    private void CalculateBaseRowCount()
    {
        baseRowCount = 0;
        int capacity = 0;

        while (capacity < objectCount)
        {
            baseRowCount++;
            capacity = baseRowCount * (baseRowCount + 1) / 2;
        }
    }

    private void RearrangePyramid(bool animate)
    {
        int index = 0;
        int row = 0;

        while (index < objects.Count)
        {
            int rowCapacity = Mathf.Max(1, baseRowCount - row);
            int objectsInRow = Mathf.Min(rowCapacity, objects.Count - index);

            for (int column = 0; column < objectsInRow; column++)
            {
                GameObject obj = objects[index];

                float x = (column - (objectsInRow - 1) / 2f)
                    * horizontalSpacing;

                float y = row * verticalSpacing;

                Vector3 targetPosition = new Vector3(x, y, 0f);

                obj.transform.DOKill();

                if (animate)
                {
                    obj.transform.DOLocalMove(targetPosition, moveDuration)
                        .SetEase(moveEase)
                        .SetDelay(index * delayBetweenObjects);
                }
                else
                {
                    obj.transform.localPosition = targetPosition;
                }

                index++;
            }

            row++;
        }
    }

    public void ClearPyramid()
    {
        foreach (GameObject obj in objects)
        {
            if (obj == null)
                continue;

            obj.transform.DOKill();

            if (Application.isPlaying)
                Destroy(obj);
            else
                DestroyImmediate(obj);
        }

        objects.Clear();
    }
}
