float2 GetResolutionScale(float2 referenceResolution)
{
    float2 refPixel = 1.0 / referenceResolution;
    float2 curPixel = 1.0 / _ScreenParams.xy;

    // Scale = how much the pixel size differs from reference
    return refPixel / curPixel;
}

float2 SampleSceneNormalBuffer(float2 uv, float3x3 viewMatrix)
{
    float3 normalWorld = SHADERGRAPH_SAMPLE_SCENE_NORMAL(uv);
    float3 normalView  = mul(viewMatrix, normalWorld);
    return normalView.xy;
}

float CalculateCurvature(float2 left, float2 right, float2 down, float2 up, float exponent, float multiplier)
{
    float total = (left.x - right.x) + (up.y - down.y);
    float curvature = 0.5 + sign(total) * pow(abs(total * multiplier), exponent);
    return clamp(curvature, 0.0, 1.0);
}

float GetCurvatureAtPoint(
    float2 uv,
    float exponent,
    float multiplier,
    float3x3 viewMatrix,
    float2 resolutionScale)
{
    // 1 pixel in UV space
    float2 pixelToUV = 1.0 / _ScreenParams.xy;

    float2 leftRight = float2(resolutionScale.x, 0) * pixelToUV;
    float2 upDown    = float2(0, resolutionScale.y) * pixelToUV;

    float2 left  = SampleSceneNormalBuffer(uv + leftRight, viewMatrix);
    float2 right = SampleSceneNormalBuffer(uv - leftRight, viewMatrix);
    float2 down  = SampleSceneNormalBuffer(uv - upDown, viewMatrix);
    float2 up    = SampleSceneNormalBuffer(uv + upDown, viewMatrix);

    return CalculateCurvature(left, right, down, up, exponent, multiplier);
}

void GetAverageCurvature_float(
    float2 screenPosition,
    int radius,
    float exponent,
    float multiplier,
    float sharpness,
    float2 _ReferenceResolution,
    out float curvature)
{
    float3x3 viewMatrix = (float3x3)UNITY_MATRIX_V;

    float2 resolutionScale = GetResolutionScale(_ReferenceResolution);

    float totalWeight = 0.0;
    curvature = 0.0;

    sharpness = clamp(1.0 - sharpness, 0.0001, 1.0);

    float2 pixelToUV = 1.0 / _ScreenParams.xy;

    for (int i = -radius; i <= radius; i++)
    {
        for (int j = -radius; j <= radius; j++)
        {
            float2 pixelOffset = float2(i, j);

            float2 uvOffset =
                (pixelOffset * resolutionScale) * pixelToUV;

            float weight = 1 / (dot(pixelOffset, pixelOffset) + sharpness);
            totalWeight += weight;

            curvature += weight *
                GetCurvatureAtPoint(
                    screenPosition + uvOffset,
                    exponent, multiplier,
                    viewMatrix,
                    resolutionScale
                );
        }
    }

    curvature /= totalWeight;
}
