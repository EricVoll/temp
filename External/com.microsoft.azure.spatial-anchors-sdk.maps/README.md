# ASA Maps Unity package

> Note: This package only supports HoloLens devices, Unity 2020.3.x, Windows XR 4.5.x or MROpenXR 1.2.x, and ARFoundation 4.1.x.

## How to install the package in your project
1) Open build settings and select **Universal Windows Platform** then click **Switch Platform**
    1) Enable **InternetClient** and **SpatialPerception** capabilities in the player settings
    2) Disable **Graphics Jobs** in the player settings
    3) Go to **XR Plug-in Management** in the player settings
        1) Check the box for **Initialize XR on Startup**
        2) If using WinXR, check the box for **Windows Mixed Reality**
        3) If using MROpenXR, check the box for **OpenXR**
            1) Check the box for **Microsoft HoloLens feature group**
            2) Go to **OpenXR** under **XR Plug-in Management**
                1) Check the box for **Microsoft HoloLens**
                2) Check the box for **Hand Tracking**
                3) Under **Interaction Profiles**, add the **Microsoft Hand Interaction Profile**
2) Download and import com.microsoft.azure.spatial-anchors-sdk.core and com.microsoft.azure.spatial-anchors-sdk.windows from [the artifact feed](https://microsoft.visualstudio.com/DefaultCollection/Analog/_artifacts/feed/mixedreality.asa.artifacts)
3) Follow [these instructions](https://docs.unity3d.com/Manual/upm-ui-local.html) to load the ASA Maps Unity package by selecting the package.json file in this directory

## How to configure the package in your project:
1) Follow [the above instructions](#how-to-install-the-package-in-your-project) to install the package in your project
2) Add the following objects to your scene (if they do not already exist):
    1) **ARAnchorManager** (will automatically add an **ARSessionOrigin**)
    2) A **Camera** with:
        1) The **Main Camera** tag
        2) **Clear Flags** set to **Solid Color**
        3) **Background** set to **(R: 0, G: 0, B:0, A:0)**
        4) The **Transform** component with **Position** set to **(X: 0, Y: 0, Z:0)**
        5) An attached **Tracked Pose Driver** component
    3) Set this new **Camera** as the **Camera** for the **ARSessionOrigin** in the inspector
3) Add an **ASASessionManager** to your scene
4) Follow [the below instructions](#how-to-reference-azure-resources-from-your-project) to reference your Azure resources
5) Follow the sample code to learn how to use  **ASASessionManager**

## How to create resources in Azure portal
1) Create an ASA account in the azure portal:
    1) Visit portal.azure.com and sign in
    2) Click **Create a resource**
    3) Search for and select **Spatial Anchors**
    4) Select the **Create** button
    5) Fill-out the details to create your ASA account
    6) On the main page of your ASA account, record the **Account Domain** and **Account ID**
    7) Select the **Access Keys** tab, record the **Primary key**

## How to reference Azure resources from your project
1) Reference your ASA account from your project:
    1) Open your project in Unity and select the **ASASessionManager** in the scene
    2) Fill-in your ASA account details that were [recorded earlier](#how-to-create-resources-in-azure-portal)