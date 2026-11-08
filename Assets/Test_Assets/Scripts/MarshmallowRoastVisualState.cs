using UnityEngine;

public class MarshmallowRoastVisualState : MonoBehaviour
{
    [Header("Colors")]
    public Color rawColor = Color.white;
    public Color roastedColor = new Color(0.58f, 0.36f, 0.20f);

    private Renderer[] renderers;
    private MaterialPropertyBlock propertyBlock;

    private void Awake()
    {
        renderers = GetComponentsInChildren<Renderer>();
        propertyBlock = new MaterialPropertyBlock();

        SetRaw();
    }

    public void SetRaw()
    {
        ApplyColor(rawColor);
    }

    public void SetRoasted()
    {
        ApplyColor(roastedColor);
    }

    private void ApplyColor(Color color)
    {
        if (renderers == null)
            return;

        foreach (Renderer r in renderers)
        {
            if (r == null)
                continue;

            r.GetPropertyBlock(propertyBlock);

            if (r.sharedMaterial != null && r.sharedMaterial.HasProperty("_BaseColor"))
            {
                propertyBlock.SetColor("_BaseColor", color);
            }
            else
            {
                propertyBlock.SetColor("_Color", color);
            }

            r.SetPropertyBlock(propertyBlock);
        }
    }
}