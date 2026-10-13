// Made with Amplify Shader Editor v1.9.9.13
// Available at the Unity Asset Store - http://u3d.as/y3X 
Shader "BOXOPHOBIC/Polyverse Skies/Standard"
{
	Properties
	{
		[StyledBanner(Polyverse Skies Standard)] _Banner( "< Banner >", Float ) = 1
		[StyledCategory(Background, 5, 10)] _BackgroundCat( "[ Background Cat ]", Float ) = 1
		[KeywordEnum( Colors,Cubemap,Combined )] _BackgroundMode( "Background Mode", Float ) = 0
		[Space(10)] _SkyColor( "Sky Color", Color ) = ( 0.4980392, 0.7450981, 1, 1 )
		_EquatorColor( "Equator Color", Color ) = ( 1, 0.747, 0, 1 )
		_GroundColor( "Ground Color", Color ) = ( 0.4980392, 0.497, 0, 1 )
		_EquatorHeight( "Equator Height", Range( 0, 1 ) ) = 0.5
		_EquatorSmoothness( "Equator Smoothness", Range( 0.01, 1 ) ) = 0.5
		[NoScaleOffset][Space(10)][StyledTextureSingleLine] _BackgroundCubemap( "Background Cubemap", CUBE ) = "black" {}
		[Space(10)] _BackgroundExposure( "Background Exposure", Range( 0, 8 ) ) = 1
		[StyledCategory(Pattern)] _PatternCat( "[ Pattern Cat ]", Float ) = 1
		[Toggle( _ENABLEPATTERNOVERLAY_ON )] _EnablePatternOverlay( "Enable Pattern Overlay", Float ) = 0
		[NoScaleOffset][Space(10)][StyledTextureSingleLine] _PatternCubemap( "Pattern Cubemap", CUBE ) = "black" {}
		[Space(10)] _PatternContrast( "Pattern Contrast", Range( 0, 1 ) ) = 0.5
		[StyledCategory(Stars)] _StarsCat( "[ Stars Cat ]", Float ) = 1
		[Toggle( _ENABLESTARS_ON )] _EnableStars( "Enable Stars", Float ) = 0
		[NoScaleOffset][Space(10)][StyledTextureSingleLine] _StarsCubemap( "Stars Cubemap", CUBE ) = "white" {}
		[Space(10)] _StarsIntensity( "Stars Intensity", Range( 0, 5 ) ) = 2
		[IntRange] _StarsLayer( "Stars Layers", Range( 1, 3 ) ) = 2
		_StarsColor( "Stars Color", Color ) = ( 1, 1, 1, 1 )
		_StarsSize( "Stars Size", Range( 0, 0.99 ) ) = 0.5
		_StarsSunMask( "Stars Sun Mask", Range( 0, 1 ) ) = 0
		_StarsHeightMask( "Stars Height Mask", Range( 0, 1 ) ) = 0
		[StyledToggle] _StarsBottomMask( "Stars Bottom Mask", Float ) = 0
		[Space(10)][Toggle( _ENABLESTARSTWINKLING_ON )] _EnableStarsTwinkling( "Enable Stars Twinkling", Float ) = 0
		[Space(10)] _TwinklingContrast( "Twinkling Contrast", Range( 0, 2 ) ) = 1
		_TwinklingSpeed( "Twinkling Speed", Float ) = 0.05
		[Space(10)][Toggle( _ENABLESTARSROTATION_ON )] _EnableStarsRotation( "Enable Stars Rotation", Float ) = 0
		[IntRange][Space(10)] _StarsRotation( "Stars Rotation", Range( 0, 360 ) ) = 360
		[StyledVector(18)] _StarsRotationAxis( "Stars Rotation Axis", Vector ) = ( 0, 1, 0, 0 )
		_StarsRotationSpeed( "Stars Rotation Speed", Float ) = 0.5
		[StyledCategory(Sun)] _SunCat( "[ Sun Cat ]", Float ) = 1
		[Toggle( _ENABLESUN_ON )] _EnableSun( "Enable Sun", Float ) = 0
		[NoScaleOffset][Space(10)][StyledTextureSingleLine] _SunTexture( "Sun Texture", 2D ) = "black" {}
		[Space(10)] _SunColor( "Sun Color", Color ) = ( 1, 1, 1, 1 )
		_SunSize( "Sun Size", Range( 0.1, 1 ) ) = 0.5
		_SunIntensity( "Sun Intensity", Range( 0, 10 ) ) = 1
		[Enum(Global From Script,0,Local From Material,1)] _SunDirectionMode( "Sun Direction", Float ) = 0
		[StyledVector(18)] _SunDirection( "Sun Direction", Vector ) = ( 0, 0, 1, 0 )
		[StyledCategory(Moon)] _MoonCat( "[ Moon Cat ]", Float ) = 1
		[Toggle( _ENABLEMOON_ON )] _EnableMoon( "Enable Moon", Float ) = 0
		[NoScaleOffset][Space(10)][StyledTextureSingleLine] _MoonTexture( "Moon Texture", 2D ) = "black" {}
		[Space(10)] _MoonColor( "Moon Color", Color ) = ( 1, 1, 1, 1 )
		_MoonSize( "Moon Size", Range( 0.1, 1 ) ) = 0.5
		_MoonIntensity( "Moon Intensity", Range( 0, 10 ) ) = 1
		[Enum(Global From Script,0,Local From Material,1)] _MoonDirectionMode( "Moon Direction", Float ) = 0
		[StyledVector(18)] _MoonDirection( "Moon Direction", Vector ) = ( 1, 0, 0, 0 )
		[StyledCategory(Clouds)] _CloudsCat( "[ Clouds Cat ]", Float ) = 1
		[Toggle( _ENABLECLOUDS_ON )] _EnableClouds( "Enable Clouds", Float ) = 0
		[NoScaleOffset][Space(10)][StyledTextureSingleLine] _CloudsCubemap( "Clouds Cubemap", CUBE ) = "black" {}
		[Space(10)] _CloudsHeight( "Clouds Height", Range( -0.5, 0.5 ) ) = 0
		_CloudsLightColor( "Clouds Light Color", Color ) = ( 1, 1, 1, 1 )
		_CloudsShadowColor( "Clouds Shadow Color", Color ) = ( 0.4980392, 0.7450981, 1, 1 )
		[Space(10)][Toggle( _ENABLECLOUDSROTATION_ON )] _EnableCloudsRotation( "Enable Clouds Rotation", Float ) = 0
		[Space(10)] _CloudsRotation( "Clouds Rotation", Range( 0, 360 ) ) = 1
		[StyledVector(18)] _CloudsRotationAxis( "Clouds Rotation Axis", Vector ) = ( 0, 1, 0, 0 )
		_CloudsRotationSpeed( "Clouds Rotation Speed", Float ) = 0.5
		[StyledCategory(Fog)] _FogCat( "[ Fog Cat ]", Float ) = 1
		[Toggle( _ENABLEBUILTINFOG_ON )] _EnableBuiltinFog( "Enable Fog", Float ) = 0
		[StyledMessage(Info, The fog color is controlled by the fog color set in the Lighting panel., _EnableBuiltinFog, 1, 10, 0)] _EnableFogMessage( "EnableFogMessage", Float ) = 0
		[Space(10)] _FogHeight( "Fog Height", Range( 0, 1 ) ) = 0
		_FogSmoothness( "Fog Smoothness", Range( 0.01, 1 ) ) = 0
		_FogFill( "Fog Fill", Range( 0, 1 ) ) = 0
		[StyledCategory(Skybox Settings)] _SkyboxCat( "[ Skybox Cat ]", Float ) = 1
		_SkyboxOffset( "Skybox Offset", Range( -1, 1 ) ) = 0
		_SkyboxRotation( "Skybox Rotation", Range( 0, 360 ) ) = 0
		[StyledVector(18)] _SkyboxRotationAxis( "Skybox Rotation Axis", Vector ) = ( 0, 1, 0, 0 )

	}
	
	SubShader
	{
		

		

		Tags { "RenderType"="Background" "Queue"="Background" "PreviewType"="Skybox" }

	LOD 100

		ZWrite Off
		ZClip False
		Cull Back
		AlphaToMask Off
		ColorMask RGBA
		Blend One Zero, One Zero
		BlendOp Add, Add
		Offset 0,0

		

		Blend Off
		

		CGINCLUDE
			#pragma target 3.0
			// ensure rendering platforms toggle list is visible

			#ifndef GLOBAL_HEADER_INCLUDED
			#define GLOBAL_HEADER_INCLUDED
			float4 ComputeClipSpacePosition( float2 screenPosNorm, float deviceDepth )
			{
				float4 positionCS = float4( screenPosNorm * 2.0 - 1.0, deviceDepth, 1.0 );
			#if UNITY_UV_STARTS_AT_TOP
				positionCS.y = -positionCS.y;
			#endif
				return positionCS;
			}
			#endif
		ENDCG

		
		Pass
		{
			
			Name "Unlit"

			Cull Back
			ZWrite Off
			ZTest LEqual
			Offset 0,0
			ColorMask RGBA
			Blend One Zero, One Zero
			

			

			CGPROGRAM
				#define ASE_VERSION 19913

				#pragma vertex vert
				#pragma fragment frag
				#pragma multi_compile_instancing
				#include "UnityCG.cginc"

				#include "UnityShaderVariables.cginc"
				#define ASE_NEEDS_VERT_POSITION
				#define ASE_NEEDS_FRAG_POSITION
				#pragma shader_feature_local _ENABLEBUILTINFOG_ON
				#pragma shader_feature_local _ENABLECLOUDS_ON
				#pragma shader_feature_local _ENABLEMOON_ON
				#pragma shader_feature_local _ENABLESUN_ON
				#pragma shader_feature_local _ENABLESTARS_ON
				#pragma shader_feature_local _ENABLEPATTERNOVERLAY_ON
				#pragma shader_feature_local _BACKGROUNDMODE_COLORS _BACKGROUNDMODE_CUBEMAP _BACKGROUNDMODE_COMBINED
				#pragma shader_feature_local _ENABLESTARSROTATION_ON
				#pragma shader_feature_local _ENABLESTARSTWINKLING_ON
				#pragma shader_feature_local _ENABLECLOUDSROTATION_ON


				#if defined(ASE_WRITE_DEPTH_CONSERVATIVE) && (SHADER_TARGET >= 45)
					#define ASE_SV_DEPTH SV_DepthLessEqual
					#define ASE_SV_POSITION_QUALIFIERS linear noperspective centroid
				#else
					#define ASE_SV_DEPTH SV_Depth
					#define ASE_SV_POSITION_QUALIFIERS
				#endif

				struct appdata
				{
					float4 vertex : POSITION;
					float3 normal : NORMAL;
					float4 tangent : TANGENT;
					
					UNITY_VERTEX_INPUT_INSTANCE_ID
				};

				struct v2f
				{
					ASE_SV_POSITION_QUALIFIERS float4 pos : SV_POSITION;
					float4 ase_texcoord : TEXCOORD0;
					float4 ase_texcoord1 : TEXCOORD1;
					float4 ase_texcoord2 : TEXCOORD2;
					float4 ase_texcoord3 : TEXCOORD3;
					float4 ase_texcoord4 : TEXCOORD4;
					float4 ase_texcoord5 : TEXCOORD5;
					float4 ase_texcoord6 : TEXCOORD6;
					UNITY_VERTEX_INPUT_INSTANCE_ID
					UNITY_VERTEX_OUTPUT_STEREO
				};

				uniform half _SunCat;
				uniform half _BackgroundCat;
				uniform half _SkyboxCat;
				uniform float _EnableFogMessage;
				uniform half _PatternCat;
				uniform half _CloudsCat;
				uniform half _Banner;
				uniform half _StarsCat;
				uniform half _FogCat;
				uniform half _MoonCat;
				uniform half4 _EquatorColor;
				uniform half4 _GroundColor;
				uniform half4 _SkyColor;
				uniform float3 _SkyboxRotationAxis;
				uniform float _SkyboxOffset;
				uniform float _SkyboxRotation;
				uniform half _EquatorHeight;
				uniform half _EquatorSmoothness;
				uniform samplerCUBE _BackgroundCubemap;
				uniform half _BackgroundExposure;
				uniform samplerCUBE _PatternCubemap;
				uniform half _PatternContrast;
				uniform half3 PolyverseSunDir;
				uniform half3 _SunDirection;
				uniform half _SunDirectionMode;
				uniform half _StarsSunMask;
				uniform half _StarsHeightMask;
				uniform half4 _StarsColor;
				uniform half _StarsIntensity;
				uniform samplerCUBE _StarsCubemap;
				uniform float3 _StarsRotationAxis;
				uniform half _StarsRotation;
				uniform half _StarsRotationSpeed;
				uniform half _StarsLayer;
				uniform half _StarsSize;
				uniform half _TwinklingSpeed;
				uniform half _TwinklingContrast;
				uniform half _StarsBottomMask;
				uniform half _SunSize;
				uniform sampler2D _SunTexture;
				uniform half4 _SunColor;
				uniform half _SunIntensity;
				uniform half3 PolyverseMoonDir;
				uniform half3 _MoonDirection;
				uniform half _MoonDirectionMode;
				uniform half _MoonSize;
				uniform sampler2D _MoonTexture;
				uniform half4 _MoonColor;
				uniform half _MoonIntensity;
				uniform half4 _CloudsShadowColor;
				uniform half4 _CloudsLightColor;
				uniform samplerCUBE _CloudsCubemap;
				uniform float3 _CloudsRotationAxis;
				uniform half _CloudsRotation;
				uniform half _CloudsRotationSpeed;
				uniform half _CloudsHeight;
				uniform half _FogHeight;
				uniform half _FogSmoothness;
				uniform half _FogFill;


				
				v2f vert( appdata v  )
				{
					UNITY_SETUP_INSTANCE_ID(v);
					v2f o;
					UNITY_INITIALIZE_OUTPUT(v2f,o);
					UNITY_TRANSFER_INSTANCE_ID(v,o);
					UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

					float3 normalizeResult46_g15 = normalize( _SkyboxRotationAxis );
					half3 RotationAxis42_g15 = normalizeResult46_g15;
					float3 appendResult1264 = (float3(0.0 , -_SkyboxOffset , 0.0));
					half3 VertexPos40_g15 = ( v.vertex.xyz + appendResult1264 );
					float dotResult48_g15 = dot( RotationAxis42_g15 , VertexPos40_g15 );
					float3 CPA50_g15 = ( RotationAxis42_g15 * dotResult48_g15 );
					half Angle44_g15 = radians( _SkyboxRotation );
					float3 normalizeResult1267 = normalize( ( CPA50_g15 + ( ( VertexPos40_g15 - CPA50_g15 ) * cos( Angle44_g15 ) ) + ( cross( RotationAxis42_g15 , ( VertexPos40_g15 - CPA50_g15 ) ) * sin( Angle44_g15 ) ) ) );
					float3 vertexToFrag1237 = normalizeResult1267;
					o.ase_texcoord.xyz = vertexToFrag1237;
					float3 ase_positionWS = mul( unity_ObjectToWorld, float4( ( v.vertex ).xyz, 1 ) ).xyz;
					o.ase_texcoord1.xyz = ase_positionWS;
					float3 normalizeResult1446 = normalize( _SunDirection );
					float3 lerpResult1443 = lerp( PolyverseSunDir , normalizeResult1446 , _SunDirectionMode);
					half3 GlobalSunDirection1005 = -lerpResult1443;
					float staticSwitch947 = ( float )0;
					#ifdef _ENABLESTARS_ON
					{
						float3 ase_viewVectorWS = ( ( unity_OrthoParams.w == 0 ) ? _WorldSpaceCameraPos - ase_positionWS : UNITY_MATRIX_V[ 2 ].xyz );
						float3 ase_viewDirWS = normalize( ase_viewVectorWS );
						float dotResult732 = dot( GlobalSunDirection1005 , ase_viewDirWS );
						float temp_output_7_0_g29 = -1.0;
						staticSwitch947 = saturate( ( ( dotResult732 - temp_output_7_0_g29 ) / ( -( 1.0 - _StarsSunMask ) - temp_output_7_0_g29 ) ) );
					}
					#else
					{
						staticSwitch947 = 0.0;
					}
					#endif
					float vertexToFrag762 = staticSwitch947;
					o.ase_texcoord.w = vertexToFrag762;
					half3 VertexPos1239 = vertexToFrag1237;
					float3 break1245 = VertexPos1239;
					float lerpResult268 = lerp( 1.0 , ( unity_OrthoParams.y / unity_OrthoParams.x ) , unity_OrthoParams.w);
					half CAMERA_MODE300 = lerpResult268;
					float temp_output_1215_0 = ( break1245.y * CAMERA_MODE300 );
					float3 appendResult1217 = (float3(break1245.x , temp_output_1215_0 , break1245.z));
					float3 staticSwitch1221 = ( float3 )0;
					#ifdef _ENABLESTARSROTATION_ON
					{
						float3 normalizeResult46_g18 = normalize( _StarsRotationAxis );
						half3 RotationAxis42_g18 = normalizeResult46_g18;
						half3 VertexPos40_g18 = appendResult1217;
						float dotResult48_g18 = dot( RotationAxis42_g18 , VertexPos40_g18 );
						float3 CPA50_g18 = ( RotationAxis42_g18 * dotResult48_g18 );
						half Angle44_g18 = 1.0;
						half3 VertexPos40_g21 = ( CPA50_g18 + ( ( VertexPos40_g18 - CPA50_g18 ) * cos( Angle44_g18 ) ) + ( cross( RotationAxis42_g18 , ( VertexPos40_g18 - CPA50_g18 ) ) * sin( Angle44_g18 ) ) );
						float3 appendResult74_g21 = (float3(0.0 , VertexPos40_g21.y , 0.0));
						float3 VertexPosRotationAxis50_g21 = appendResult74_g21;
						float3 break84_g21 = VertexPos40_g21;
						float3 appendResult81_g21 = (float3(break84_g21.x , 0.0 , break84_g21.z));
						float3 VertexPosOtherAxis82_g21 = appendResult81_g21;
						half Angle44_g21 = -radians( ( _StarsRotation + ( _Time.y * _StarsRotationSpeed ) ) );
						staticSwitch1221 = ( VertexPosRotationAxis50_g21 + ( VertexPosOtherAxis82_g21 * cos( Angle44_g21 ) ) + ( cross( float3( 0, 1, 0 ) , VertexPosOtherAxis82_g21 ) * sin( Angle44_g21 ) ) );
					}
					#else
					{
						staticSwitch1221 = appendResult1217;
					}
					#endif
					float3 vertexToFrag1220 = staticSwitch1221;
					o.ase_texcoord3.xyz = vertexToFrag1220;
					float staticSwitch953 = ( float )0;
					#ifdef _ENABLESTARS_ON
					{
						staticSwitch953 = saturate(  (0.1 + ( abs( v.vertex.xyz.y ) - 0.0 ) * ( 1.0 - 0.1 ) / ( _StarsHeightMask - 0.0 ) ) );
					}
					#else
					{
						staticSwitch953 = 0.0;
					}
					#endif
					float vertexToFrag856 = staticSwitch953;
					o.ase_texcoord1.w = vertexToFrag856;
					float2 staticSwitch940 = ( float2 )0;
					#ifdef _ENABLESUN_ON
					{
						float3 temp_output_962_0 = cross( -lerpResult1443 , half3( 0, 1, 0 ) );
						float3 normalizeResult967 = normalize( temp_output_962_0 );
						float dotResult968 = dot( normalizeResult967 , v.vertex.xyz );
						float3 normalizeResult965 = normalize( cross( GlobalSunDirection1005 , temp_output_962_0 ) );
						float dotResult969 = dot( normalizeResult965 , v.vertex.xyz );
						float2 appendResult970 = (float2(dotResult968 , dotResult969));
						float2 break972 = appendResult970;
						float2 appendResult980 = (float2(break972.x , ( break972.y * CAMERA_MODE300 )));
						float lerpResult1272 = lerp( 20.0 , 2.0 , _SunSize);
						staticSwitch940 = (( appendResult980 * lerpResult1272 )*0.5 + 0.5);
					}
					#else
					{
						staticSwitch940 = float2( 0,0 );
					}
					#endif
					float2 vertexToFrag993 = staticSwitch940;
					o.ase_texcoord4.xy = vertexToFrag993;
					float staticSwitch1027 = ( float )0;
					#ifdef _ENABLESUN_ON
					{
						float dotResult988 = dot( GlobalSunDirection1005 , v.vertex.xyz );
						staticSwitch1027 = saturate( dotResult988 );
					}
					#else
					{
						staticSwitch1027 = 0.0;
					}
					#endif
					float vertexToFrag997 = staticSwitch1027;
					o.ase_texcoord3.w = vertexToFrag997;
					float3 normalizeResult1447 = normalize( _MoonDirection );
					float3 lerpResult1451 = lerp( PolyverseMoonDir , normalizeResult1447 , _MoonDirectionMode);
					half3 GlobalMoonDirection1073 = -lerpResult1451;
					float2 staticSwitch1057 = ( float2 )0;
					#ifdef _ENABLEMOON_ON
					{
						float3 temp_output_1058_0 = cross( -lerpResult1451 , half3( 0, 1, 0 ) );
						float3 normalizeResult1039 = normalize( temp_output_1058_0 );
						float dotResult1036 = dot( normalizeResult1039 , v.vertex.xyz );
						float3 normalizeResult1064 = normalize( cross( GlobalMoonDirection1073 , temp_output_1058_0 ) );
						float dotResult1067 = dot( normalizeResult1064 , v.vertex.xyz );
						float2 appendResult1066 = (float2(dotResult1036 , dotResult1067));
						float2 break1063 = appendResult1066;
						float2 appendResult1069 = (float2(break1063.x , ( break1063.y * CAMERA_MODE300 )));
						float lerpResult1285 = lerp( 20.0 , 2.0 , _MoonSize);
						staticSwitch1057 = (( appendResult1069 * lerpResult1285 )*0.5 + 0.5);
					}
					#else
					{
						staticSwitch1057 = float2( 0,0 );
					}
					#endif
					float2 vertexToFrag1043 = staticSwitch1057;
					o.ase_texcoord4.zw = vertexToFrag1043;
					float staticSwitch1052 = ( float )0;
					#ifdef _ENABLEMOON_ON
					{
						float dotResult1054 = dot( GlobalMoonDirection1073 , v.vertex.xyz );
						staticSwitch1052 = saturate( dotResult1054 );
					}
					#else
					{
						staticSwitch1052 = 0.0;
					}
					#endif
					float vertexToFrag1051 = staticSwitch1052;
					o.ase_texcoord5.x = vertexToFrag1051;
					float3 normalizeResult46_g31 = normalize( _CloudsRotationAxis );
					half3 RotationAxis42_g31 = normalizeResult46_g31;
					float3 break1247 = VertexPos1239;
					float3 appendResult1129 = (float3(break1247.x , ( break1247.y * CAMERA_MODE300 ) , break1247.z));
					half3 VertexPos40_g31 = appendResult1129;
					float dotResult48_g31 = dot( RotationAxis42_g31 , VertexPos40_g31 );
					float3 CPA50_g31 = ( RotationAxis42_g31 * dotResult48_g31 );
					half Angle44_g31 = 1.0;
					half3 VertexPos40_g32 = ( CPA50_g31 + ( ( VertexPos40_g31 - CPA50_g31 ) * cos( Angle44_g31 ) ) + ( cross( RotationAxis42_g31 , ( VertexPos40_g31 - CPA50_g31 ) ) * sin( Angle44_g31 ) ) );
					float3 appendResult74_g32 = (float3(0.0 , VertexPos40_g32.y , 0.0));
					float3 VertexPosRotationAxis50_g32 = appendResult74_g32;
					float3 break84_g32 = VertexPos40_g32;
					float3 appendResult81_g32 = (float3(break84_g32.x , 0.0 , break84_g32.z));
					float3 VertexPosOtherAxis82_g32 = appendResult81_g32;
					half Angle44_g32 = -radians( ( _CloudsRotation + ( _Time.y * _CloudsRotationSpeed ) ) );
					float3 temp_output_1378_19 = ( VertexPosRotationAxis50_g32 + ( VertexPosOtherAxis82_g32 * cos( Angle44_g32 ) ) + ( cross( float3( 0, 1, 0 ) , VertexPosOtherAxis82_g32 ) * sin( Angle44_g32 ) ) );
					float3 vertexToFrag1207 = temp_output_1378_19;
					o.ase_texcoord5.yzw = vertexToFrag1207;
					float3 vertexToFrag1222 = appendResult1129;
					o.ase_texcoord6.xyz = vertexToFrag1222;
					
					o.ase_texcoord2 = v.vertex;
					
					//setting value to unused interpolator channels and avoid initialization warnings
					o.ase_texcoord6.w = 0;

					#ifdef ASE_ABSOLUTE_VERTEX_POS
						float3 defaultVertexValue = v.vertex.xyz;
					#else
						float3 defaultVertexValue = float3(0, 0, 0);
					#endif
					float3 vertexValue = defaultVertexValue;
					#ifdef ASE_ABSOLUTE_VERTEX_POS
						v.vertex.xyz = vertexValue;
					#else
						v.vertex.xyz += vertexValue;
					#endif
					v.vertex.w = 1;
					v.normal = v.normal;
					v.tangent = v.tangent;

					o.pos = UnityObjectToClipPos( v.vertex );

					#if defined( ASE_SHADOWS )
						UNITY_TRANSFER_SHADOW( o, v.texcoord );
					#endif
					return o;
				}

				half4 frag( v2f IN 
							#if defined( ASE_WRITE_DEPTH )
								, out float outputDepth : SV_Depth
							#endif
				) : SV_Target
				{
					UNITY_SETUP_INSTANCE_ID( IN );
					UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX( IN );

					float4 ScreenPosNorm = float4( IN.pos.xy * ( _ScreenParams.zw - 1.0 ), IN.pos.zw );
					float4 ClipPos = ComputeClipSpacePosition( ScreenPosNorm.xy, IN.pos.z ) * IN.pos.w;
					float4 ScreenPos = ComputeScreenPos( ClipPos );

					float3 vertexToFrag1237 = IN.ase_texcoord.xyz;
					half3 VertexPos1239 = vertexToFrag1237;
					float3 lerpResult180 = lerp( _GroundColor.rgb , _SkyColor.rgb , step( 0.0 , VertexPos1239.y ));
					float temp_output_7_0_g30 = 0.0;
					float3 lerpResult288 = lerp( _EquatorColor.rgb , lerpResult180 , pow( saturate( ( ( abs( VertexPos1239.y ) - temp_output_7_0_g30 ) / ( _EquatorHeight - temp_output_7_0_g30 ) ) ) , ( 1.0 - _EquatorSmoothness ) ));
					half3 SKY218 = lerpResult288;
					half3 BACKGROUND1195 = ( texCUBE( _BackgroundCubemap, VertexPos1239 ).rgb * _BackgroundExposure );
					float3 staticSwitch1200 = ( float3 )0;
					#if defined( _BACKGROUNDMODE_COLORS )
					{
						staticSwitch1200 = SKY218;
					}
					#elif defined( _BACKGROUNDMODE_CUBEMAP )
					{
						staticSwitch1200 = BACKGROUND1195;
					}
					#elif defined( _BACKGROUNDMODE_COMBINED )
					{
						staticSwitch1200 = ( SKY218 * BACKGROUND1195 );
					}
					#else
					{
						staticSwitch1200 = SKY218;
					}
					#endif
					float3 staticSwitch524 = ( float3 )0;
					#ifdef _ENABLEPATTERNOVERLAY_ON
					{
						float lerpResult1380 = lerp( 0.5 , texCUBE( _PatternCubemap, VertexPos1239 ).r , _PatternContrast);
						half PATTERN513 = saturate( lerpResult1380 );
						float3 temp_cast_0 = (PATTERN513).xxx;
						float3 blendOpSrc574 = temp_cast_0;
						float3 blendOpDest574 = staticSwitch1200;
						staticSwitch524 = ( saturate( (( blendOpDest574 > 0.5 ) ? ( 1.0 - 2.0 * ( 1.0 - blendOpDest574 ) * ( 1.0 - blendOpSrc574 ) ) : ( 2.0 * blendOpDest574 * blendOpSrc574 ) ) ));
					}
					#else
					{
						staticSwitch524 = staticSwitch1200;
					}
					#endif
					float3 normalizeResult1446 = normalize( _SunDirection );
					float3 lerpResult1443 = lerp( PolyverseSunDir , normalizeResult1446 , _SunDirectionMode);
					half3 GlobalSunDirection1005 = -lerpResult1443;
					float staticSwitch947 = ( float )0;
					#ifdef _ENABLESTARS_ON
					{
						float3 ase_positionWS = IN.ase_texcoord1.xyz;
						float3 ase_viewVectorWS = ( ( unity_OrthoParams.w == 0 ) ? _WorldSpaceCameraPos - ase_positionWS : UNITY_MATRIX_V[ 2 ].xyz );
						float3 ase_viewDirWS = normalize( ase_viewVectorWS );
						float dotResult732 = dot( GlobalSunDirection1005 , ase_viewDirWS );
						float temp_output_7_0_g29 = -1.0;
						staticSwitch947 = saturate( ( ( dotResult732 - temp_output_7_0_g29 ) / ( -( 1.0 - _StarsSunMask ) - temp_output_7_0_g29 ) ) );
					}
					#else
					{
						staticSwitch947 = 0.0;
					}
					#endif
					float staticSwitch953 = ( float )0;
					#ifdef _ENABLESTARS_ON
					{
						staticSwitch953 = saturate(  (0.1 + ( abs( IN.ase_texcoord2.xyz.y ) - 0.0 ) * ( 1.0 - 0.1 ) / ( _StarsHeightMask - 0.0 ) ) );
					}
					#else
					{
						staticSwitch953 = 0.0;
					}
					#endif
					float3 break1245 = VertexPos1239;
					float lerpResult268 = lerp( 1.0 , ( unity_OrthoParams.y / unity_OrthoParams.x ) , unity_OrthoParams.w);
					half CAMERA_MODE300 = lerpResult268;
					float temp_output_1215_0 = ( break1245.y * CAMERA_MODE300 );
					float3 staticSwitch918 = ( float3 )0;
					#ifdef _ENABLESTARS_ON
					{
						half3 STARS1453 = ( _StarsColor.rgb * _StarsIntensity );
						float vertexToFrag762 = IN.ase_texcoord.w;
						float3 vertexToFrag1220 = IN.ase_texcoord3.xyz;
						float4 texCUBENode564 = texCUBE( _StarsCubemap, vertexToFrag1220 );
						float temp_output_537_0 =  (3.0 + ( _StarsLayer - 1.0 ) * ( 1.0 - 3.0 ) / ( 3.0 - 1.0 ) );
						float staticSwitch878 = ( float )0;
						#ifdef _ENABLESTARSTWINKLING_ON
						{
							float mulTime1417 = _Time.y * ( _TwinklingSpeed * 10.0 );
							float mulTime1437 = _Time.y * ( _TwinklingSpeed * 5.0 );
							float saferPower554 = abs( ( ( sin( ( ( ( IN.ase_texcoord2.xyz.x + IN.ase_texcoord2.xyz.y ) * 20.0 ) + mulTime1417 ) ) * sin( ( mulTime1437 + ( ( IN.ase_texcoord2.xyz.x + IN.ase_texcoord2.xyz.z ) * 30.0 ) ) ) ) + 0.5 ) );
							staticSwitch878 = saturate( pow( saferPower554 , _TwinklingContrast ) );
						}
						#else
						{
							staticSwitch878 = 1.0;
						}
						#endif
						float vertexToFrag856 = IN.ase_texcoord1.w;
						half Starts_Bottom_Mask1250 = step( 0.0 , temp_output_1215_0 );
						float lerpResult1255 = lerp( 1.0 , saturate( Starts_Bottom_Mask1250 ) , _StarsBottomMask);
						half STARS_MASK630 = ( floor( ( vertexToFrag762 * ( ( texCUBENode564.r + ( texCUBENode564.g * step( temp_output_537_0 , 2.0 ) ) + ( texCUBENode564.b * step( temp_output_537_0 , 1.0 ) ) ) + ( _StarsSize * staticSwitch878 ) ) * vertexToFrag856 ) ) * saturate( _StarsIntensity ) * lerpResult1255 );
						float3 lerpResult1455 = lerp( staticSwitch524 , STARS1453 , STARS_MASK630);
						staticSwitch918 = lerpResult1455;
					}
					#else
					{
						staticSwitch918 = staticSwitch524;
					}
					#endif
					float staticSwitch1027 = ( float )0;
					#ifdef _ENABLESUN_ON
					{
						float dotResult988 = dot( GlobalSunDirection1005 , IN.ase_texcoord2.xyz );
						staticSwitch1027 = saturate( dotResult988 );
					}
					#else
					{
						staticSwitch1027 = 0.0;
					}
					#endif
					float2 staticSwitch940 = ( float2 )0;
					#ifdef _ENABLESUN_ON
					{
						float3 temp_output_962_0 = cross( -lerpResult1443 , half3( 0, 1, 0 ) );
						float3 normalizeResult967 = normalize( temp_output_962_0 );
						float dotResult968 = dot( normalizeResult967 , IN.ase_texcoord2.xyz );
						float3 normalizeResult965 = normalize( cross( GlobalSunDirection1005 , temp_output_962_0 ) );
						float dotResult969 = dot( normalizeResult965 , IN.ase_texcoord2.xyz );
						float2 appendResult970 = (float2(dotResult968 , dotResult969));
						float2 break972 = appendResult970;
						float2 appendResult980 = (float2(break972.x , ( break972.y * CAMERA_MODE300 )));
						float lerpResult1272 = lerp( 20.0 , 2.0 , _SunSize);
						staticSwitch940 = (( appendResult980 * lerpResult1272 )*0.5 + 0.5);
					}
					#else
					{
						staticSwitch940 = float2( 0,0 );
					}
					#endif
					float3 staticSwitch919 = ( float3 )0;
					#ifdef _ENABLESUN_ON
					{
						float2 vertexToFrag993 = IN.ase_texcoord4.xy;
						float4 tex2DNode995 = tex2D( _SunTexture, vertexToFrag993 );
						half3 SUN1004 = ( tex2DNode995.r * _SunColor.rgb * max( _SunIntensity, 1.0 ) );
						float vertexToFrag997 = IN.ase_texcoord3.w;
						half SUN_MASK1003 = ( tex2DNode995.a * saturate( _SunIntensity ) * vertexToFrag997 );
						float3 lerpResult176 = lerp( staticSwitch918 , SUN1004 , SUN_MASK1003);
						staticSwitch919 = lerpResult176;
					}
					#else
					{
						staticSwitch919 = staticSwitch918;
					}
					#endif
					float3 normalizeResult1447 = normalize( _MoonDirection );
					float3 lerpResult1451 = lerp( PolyverseMoonDir , normalizeResult1447 , _MoonDirectionMode);
					half3 GlobalMoonDirection1073 = -lerpResult1451;
					float staticSwitch1052 = ( float )0;
					#ifdef _ENABLEMOON_ON
					{
						float dotResult1054 = dot( GlobalMoonDirection1073 , IN.ase_texcoord2.xyz );
						staticSwitch1052 = saturate( dotResult1054 );
					}
					#else
					{
						staticSwitch1052 = 0.0;
					}
					#endif
					float2 staticSwitch1057 = ( float2 )0;
					#ifdef _ENABLEMOON_ON
					{
						float3 temp_output_1058_0 = cross( -lerpResult1451 , half3( 0, 1, 0 ) );
						float3 normalizeResult1039 = normalize( temp_output_1058_0 );
						float dotResult1036 = dot( normalizeResult1039 , IN.ase_texcoord2.xyz );
						float3 normalizeResult1064 = normalize( cross( GlobalMoonDirection1073 , temp_output_1058_0 ) );
						float dotResult1067 = dot( normalizeResult1064 , IN.ase_texcoord2.xyz );
						float2 appendResult1066 = (float2(dotResult1036 , dotResult1067));
						float2 break1063 = appendResult1066;
						float2 appendResult1069 = (float2(break1063.x , ( break1063.y * CAMERA_MODE300 )));
						float lerpResult1285 = lerp( 20.0 , 2.0 , _MoonSize);
						staticSwitch1057 = (( appendResult1069 * lerpResult1285 )*0.5 + 0.5);
					}
					#else
					{
						staticSwitch1057 = float2( 0,0 );
					}
					#endif
					float3 staticSwitch1113 = ( float3 )0;
					#ifdef _ENABLEMOON_ON
					{
						float2 vertexToFrag1043 = IN.ase_texcoord4.zw;
						float4 tex2DNode1049 = tex2D( _MoonTexture, vertexToFrag1043 );
						half3 MOON1077 = ( tex2DNode1049.r * _MoonColor.rgb * max( _MoonIntensity, 1.0 ) );
						float vertexToFrag1051 = IN.ase_texcoord5.x;
						half MOON_MASK1078 = ( tex2DNode1049.a * vertexToFrag1051 * saturate( _MoonIntensity ) );
						float3 lerpResult1114 = lerp( staticSwitch919 , MOON1077 , MOON_MASK1078);
						staticSwitch1113 = lerpResult1114;
					}
					#else
					{
						staticSwitch1113 = staticSwitch919;
					}
					#endif
					float3 staticSwitch1120 = ( float3 )0;
					#ifdef _ENABLECLOUDS_ON
					{
						float3 break1247 = VertexPos1239;
						float3 appendResult1129 = (float3(break1247.x , ( break1247.y * CAMERA_MODE300 ) , break1247.z));
						float3 staticSwitch1164 = ( float3 )0;
						#ifdef _ENABLECLOUDSROTATION_ON
						{
							float3 vertexToFrag1207 = IN.ase_texcoord5.yzw;
							staticSwitch1164 = vertexToFrag1207;
						}
						#else
						{
							float3 vertexToFrag1222 = IN.ase_texcoord6.xyz;
							staticSwitch1164 = vertexToFrag1222;
						}
						#endif
						float3 break245 = staticSwitch1164;
						float3 appendResult246 = (float3(break245.x , ( break245.y + ( _CloudsHeight * -1.0 ) ) , break245.z));
						float4 texCUBENode41 = texCUBE( _CloudsCubemap, appendResult246 );
						float3 lerpResult101 = lerp( _CloudsShadowColor.rgb , _CloudsLightColor.rgb , texCUBENode41.rgb);
						half3 CLOUDS222 = lerpResult101;
						half CLOUDS_MASK1406 = texCUBENode41.a;
						float3 lerpResult227 = lerp( staticSwitch1113 , CLOUDS222 , CLOUDS_MASK1406);
						staticSwitch1120 = lerpResult227;
					}
					#else
					{
						staticSwitch1120 = staticSwitch1113;
					}
					#endif
					float3 staticSwitch921 = ( float3 )0;
					#ifdef _ENABLEBUILTINFOG_ON
					{
						float temp_output_7_0_g33 = 0.0;
						float lerpResult678 = lerp( saturate( pow( ( ( abs( VertexPos1239.y ) - temp_output_7_0_g33 ) / ( _FogHeight - temp_output_7_0_g33 ) ) , ( 1.0 - _FogSmoothness ) ) ) , 0.0 , _FogFill);
						half FOG_MASK359 = lerpResult678;
						float3 lerpResult317 = lerp( (unity_FogColor).rgb , staticSwitch1120 , FOG_MASK359);
						staticSwitch921 = lerpResult317;
					}
					#else
					{
						staticSwitch921 = staticSwitch1120;
					}
					#endif
					

					float3 Color = staticSwitch921;
					float Alpha = 1;
					half AlphaClipThreshold = 0.5;
					half AlphaClipThresholdShadow = 0.5;

					#if defined( ASE_WRITE_DEPTH )
						outputDepth = IN.pos.z;
					#endif

					#ifdef _ALPHATEST_ON
						clip( Alpha - AlphaClipThreshold );
					#endif

				#if defined( ASE_SURFACE_TRANSPARENT ) || defined( ASE_OPAQUE_KEEP_ALPHA )
					return half4( Color, Alpha );
				#else
					return half4( Color, 1.0 );
				#endif
				}
			ENDCG
		}

	
	}
	
	CustomEditor "PolyverseSkiesAsset.MaterialGUI"
	
	Fallback Off
}
/*ASEBEGIN
Version=19913
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1265,"pos":[-896,-5488],"params":["Inherit","False","Property","_SkyboxOffset","Skybox Offset","64","0","Create","True","0","0","0","False","0","False","Object","-1","","0","0","-1","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.OrthoParams, AmplifyShaderEditor","id":267,"pos":[-896,-6016],"params":["Inherit","False","0","5","FLOAT4","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.PosVertexDataNode, AmplifyShaderEditor","id":1415,"pos":[-896,-896],"params":["Inherit","False","0","0","5","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":546,"pos":[-896,-704],"params":["Half","False","Property","_TwinklingSpeed","Twinkling Speed","26","0","Create","True","0","0","0","False","0","False","Object","-1","","0.05","0.05","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.PosVertexDataNode, AmplifyShaderEditor","id":1426,"pos":[-896,-576],"params":["Inherit","False","0","0","5","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.NegateNode, AmplifyShaderEditor","id":1269,"pos":[-608,-5488],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleDivideOpNode, AmplifyShaderEditor","id":309,"pos":[-592,-6016],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1007,"pos":[-448,-6016],"params":["Half","False","Constant","_Float7","Float 7","47","0","Create","True","0","0","0","False","0","False","Object","-1","","1","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.Vector3Node, AmplifyShaderEditor","id":1444,"pos":[-1408,32],"params":["Half","False","Property","_SunDirection","Sun Direction","38","0","Create","True","0","0","0","False","1","StyledVector(18)","False","Object","-1","","0,0,1","0,0,0","0","4","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3"]}
{"type":"AmplifyShaderEditor.SimpleTimeNode, AmplifyShaderEditor","id":1209,"pos":[-256,-1280],"params":["Inherit","False","1","0","FLOAT","1","False","5","FLOAT","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1208,"pos":[-256,-1216],"params":["Half","False","Property","_StarsRotationSpeed","Stars Rotation Speed","30","0","Create","True","0","0","0","False","0","False","Object","-1","","0.5","0.5","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleAddOpNode, AmplifyShaderEditor","id":1416,"pos":[-640,-896],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleAddOpNode, AmplifyShaderEditor","id":1425,"pos":[-640,-576],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1433,"pos":[-640,-768],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","10","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1438,"pos":[-640,-672],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","5","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.Vector3Node, AmplifyShaderEditor","id":1448,"pos":[-1408,1040],"params":["Half","False","Property","_MoonDirection","Moon Direction","46","0","Create","True","0","0","0","False","1","StyledVector(18)","False","Object","-1","","1,0,0","0,0,0","0","4","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1233,"pos":[-896,-5216],"params":["Inherit","False","Property","_SkyboxRotation","Skybox Rotation","65","0","Create","True","0","0","0","False","0","False","Object","-1","","0","0","0","360","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.PosVertexDataNode, AmplifyShaderEditor","id":1234,"pos":[-896,-5632],"params":["Inherit","False","0","0","5","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.DynamicAppendNode, AmplifyShaderEditor","id":1264,"pos":[-448,-5568],"params":["Inherit","False","FLOAT3","4","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":268,"pos":[-256,-6016],"params":["Inherit","False","3","0","FLOAT","1","False","1","FLOAT","0.5","False","2","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.NormalizeNode, AmplifyShaderEditor","id":1446,"pos":[-1232,32],"params":["Inherit","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1445,"pos":[-1408,192],"params":["Half","False","Property","_SunDirectionMode","Sun Direction","37","1","[Enum]","Create","False","0","2","Global From Script","0","Local From Material","1","0","False","0","False","Object","-1","","0","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.Vector3Node, AmplifyShaderEditor","id":938,"pos":[-1408,-128],"params":["Half","False","Global","PolyverseSunDir","PolyverseSunDir","38","0","Create","True","0","0","0","False","0","False","Object","-1","","0,0,0","0,0,0","0","4","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1210,"pos":[0,-1280],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1211,"pos":[-256,-1376],"params":["Half","False","Property","_StarsRotation","Stars Rotation","28","1","[IntRange]","Create","True","0","0","0","False","1","Space(10)","False","Object","-1","","360","0","0","360","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1421,"pos":[-384,-896],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","20","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleTimeNode, AmplifyShaderEditor","id":1417,"pos":[-384,-768],"params":["Inherit","False","1","0","FLOAT","1","False","5","FLOAT","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.SimpleTimeNode, AmplifyShaderEditor","id":1437,"pos":[-384,-672],"params":["Inherit","False","1","0","FLOAT","1","False","5","FLOAT","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1424,"pos":[-384,-576],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","30","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.NormalizeNode, AmplifyShaderEditor","id":1447,"pos":[-1216,1040],"params":["Inherit","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1449,"pos":[-1408,1216],"params":["Half","False","Property","_MoonDirectionMode","Moon Direction","45","1","[Enum]","Create","False","0","2","Global From Script","0","Local From Material","1","0","False","0","False","Object","-1","","0","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.Vector3Node, AmplifyShaderEditor","id":1061,"pos":[-1408,896],"params":["Half","False","Global","PolyverseMoonDir","PolyverseMoonDir","38","0","Create","True","0","0","0","False","0","False","Object","-1","","0,0,0","0,0,0","0","4","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3"]}
{"type":"AmplifyShaderEditor.VertexToFragmentNode, AmplifyShaderEditor","id":1237,"pos":[512,-5632],"params":["Inherit","False","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RadiansOpNode, AmplifyShaderEditor","id":1293,"pos":[-608,-5216],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleAddOpNode, AmplifyShaderEditor","id":1262,"pos":[-256,-5632],"params":["Inherit","False","2","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.Vector3Node, AmplifyShaderEditor","id":1238,"pos":[-896,-5376],"params":["Inherit","False","Property","_SkyboxRotationAxis","Skybox Rotation Axis","66","0","Create","True","0","0","0","False","1","StyledVector(18)","False","Object","-1","","0,1,0","0,1,0","0","4","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":300,"pos":[0,-6016],"params":["Half","False","CAMERA_MODE","-1","True","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":1443,"pos":[-1088,-128],"params":["Inherit","False","3","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.SimpleAddOpNode, AmplifyShaderEditor","id":1213,"pos":[128,-1376],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.Vector3Node, AmplifyShaderEditor","id":961,"pos":[-1136,208],"params":["Half","False","Constant","_Vector2","Vector 2","9","0","Create","True","0","0","0","False","0","False","Object","-1","","0,1,0","0,0,0","0","4","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3"]}
{"type":"AmplifyShaderEditor.SimpleAddOpNode, AmplifyShaderEditor","id":1422,"pos":[-128,-896],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleAddOpNode, AmplifyShaderEditor","id":1427,"pos":[-128,-576],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":1451,"pos":[-1056,896],"params":["Inherit","False","3","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":1239,"pos":[768,-5632],"params":["Half","False","VertexPos","-1","True","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.FunctionNode, AmplifyShaderEditor","id":1368,"pos":[0,-5632],"params":["Inherit","False","Compute Rotation","-1","","15","d0fbe60918b2bc146b660122744f3d02","0","4","38","FLOAT3","0,0,0","False","72","FLOAT3","0,0,0","False","41","FLOAT3","0,0,0","False","43","FLOAT","0","False","1","FLOAT3","19"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1244,"pos":[-896,-1792],"params":["Inherit","False","1239","VertexPos","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1212,"pos":[-896,-1648],"params":["Inherit","False","300","CAMERA_MODE","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.NegateNode, AmplifyShaderEditor","id":1442,"pos":[-896,-128],"params":["Inherit","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RadiansOpNode, AmplifyShaderEditor","id":1216,"pos":[256,-1376],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.CrossProductOpNode, AmplifyShaderEditor","id":962,"pos":[-576,0],"params":["Inherit","False","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.Vector3Node, AmplifyShaderEditor","id":1228,"pos":[-256,-1536],"params":["Inherit","False","Property","_StarsRotationAxis","Stars Rotation Axis","29","0","Create","True","0","0","0","False","1","StyledVector(18)","False","Object","-1","","0,1,0","1,1,9.48","0","4","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3"]}
{"type":"AmplifyShaderEditor.CrossProductOpNode, AmplifyShaderEditor","id":964,"pos":[-384,-128],"params":["Inherit","False","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.Vector3Node, AmplifyShaderEditor","id":1062,"pos":[-896,1056],"params":["Half","False","Constant","_Vector3","Vector 3","9","0","Create","True","0","0","0","False","0","False","Object","-1","","0,1,0","0,0,0","0","4","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3"]}
{"type":"AmplifyShaderEditor.SinOpNode, AmplifyShaderEditor","id":1428,"pos":[0,-576],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SinOpNode, AmplifyShaderEditor","id":1418,"pos":[0,-896],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.NegateNode, AmplifyShaderEditor","id":1450,"pos":[-896,896],"params":["Inherit","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":1073,"pos":[-640,896],"params":["Half","False","GlobalMoonDirection","-1","True","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.NormalizeNode, AmplifyShaderEditor","id":1267,"pos":[320,-5632],"params":["Inherit","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.BreakToComponentsNode, AmplifyShaderEditor","id":1245,"pos":[-736,-1792],"params":["Inherit","False","FLOAT3","1","0","FLOAT3","0,0,0","False","16","FLOAT","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT","5","FLOAT","6","FLOAT","7","FLOAT","8","FLOAT","9","FLOAT","10","FLOAT","11","FLOAT","12","FLOAT","13","FLOAT","14","FLOAT","15"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1215,"pos":[-640,-1664],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":1005,"pos":[-640,-128],"params":["Half","False","GlobalSunDirection","-1","True","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.FunctionNode, AmplifyShaderEditor","id":1369,"pos":[128,-1664],"params":["Inherit","False","Compute Rotation","-1","","18","d0fbe60918b2bc146b660122744f3d02","0","4","38","FLOAT3","0,0,0","False","72","FLOAT3","0,0,0","False","41","FLOAT3","0,0,0","False","43","FLOAT","1","False","1","FLOAT3","19"]}
{"type":"AmplifyShaderEditor.NegateNode, AmplifyShaderEditor","id":1218,"pos":[384,-1376],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":733,"pos":[2688,-1472],"params":["Half","False","Property","_StarsSunMask","Stars Sun Mask","21","0","Create","True","0","0","0","False","0","False","Object","-1","","0","0","0","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.CrossProductOpNode, AmplifyShaderEditor","id":1058,"pos":[-576,1024],"params":["Inherit","False","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.PosVertexDataNode, AmplifyShaderEditor","id":1173,"pos":[-384,256],"params":["Inherit","False","0","0","5","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.NormalizeNode, AmplifyShaderEditor","id":965,"pos":[-192,-128],"params":["Inherit","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.NormalizeNode, AmplifyShaderEditor","id":967,"pos":[-384,0],"params":["Inherit","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1431,"pos":[256,-896],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.CrossProductOpNode, AmplifyShaderEditor","id":1038,"pos":[-384,896],"params":["Inherit","False","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":260,"pos":[-896,2624],"params":["Half","False","Property","_CloudsRotationSpeed","Clouds Rotation Speed","56","0","Create","True","0","0","0","False","0","False","Object","-1","","0.5","-0.24","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleTimeNode, AmplifyShaderEditor","id":701,"pos":[-896,2560],"params":["Inherit","False","1","0","FLOAT","1","False","5","FLOAT","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.DynamicAppendNode, AmplifyShaderEditor","id":1217,"pos":[-512,-1792],"params":["Inherit","False","FLOAT3","4","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.FunctionNode, AmplifyShaderEditor","id":1370,"pos":[512,-1664],"params":["Inherit","False","Compute Rotation Y","-1","","21","848a7021c0b62984d9ca063f51e19d69","0","2","38","FLOAT3","0,0,0","False","43","FLOAT","0","False","1","FLOAT3","19"]}
{"type":"AmplifyShaderEditor.DotProductOpNode, AmplifyShaderEditor","id":969,"pos":[0,-128],"params":["Inherit","False","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1148,"pos":[2432,-1792],"params":["Inherit","False","1005","GlobalSunDirection","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.ViewDirInputsCoordNode, AmplifyShaderEditor","id":731,"pos":[2432,-1648],"params":["Float","False","World","False","0","4","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3"]}
{"type":"AmplifyShaderEditor.PosVertexDataNode, AmplifyShaderEditor","id":1172,"pos":[2432,-1152],"params":["Inherit","False","0","0","5","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1011,"pos":[1024,-1376],"params":["Half","False","Constant","_Float11","Float 11","47","0","Create","True","0","0","0","False","0","False","Object","-1","","3","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.OneMinusNode, AmplifyShaderEditor","id":738,"pos":[3008,-1472],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.DotProductOpNode, AmplifyShaderEditor","id":968,"pos":[0,0],"params":["Inherit","False","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1010,"pos":[1024,-1456],"params":["Half","False","Constant","_Float10","Float 10","47","0","Create","True","0","0","0","False","0","False","Object","-1","","1","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":528,"pos":[1024,-1536],"params":["Half","False","Property","_StarsLayer","Stars Layers","18","1","[IntRange]","Create","False","0","0","0","False","0","False","Object","-1","","2","2","1","3","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleAddOpNode, AmplifyShaderEditor","id":1440,"pos":[448,-896],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0.5","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":555,"pos":[256,-640],"params":["Half","False","Property","_TwinklingContrast","Twinkling Contrast","25","0","Create","True","0","0","0","False","1","Space(10)","False","Object","-1","","1","1","0","2","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.NormalizeNode, AmplifyShaderEditor","id":1064,"pos":[-192,896],"params":["Inherit","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.NormalizeNode, AmplifyShaderEditor","id":1039,"pos":[-384,1024],"params":["Inherit","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.PosVertexDataNode, AmplifyShaderEditor","id":1175,"pos":[-384,1280],"params":["Inherit","False","0","0","5","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":48,"pos":[-896,2464],"params":["Half","False","Property","_CloudsRotation","Clouds Rotation","54","0","Create","True","0","0","0","False","1","Space(10)","False","Object","-1","","1","360","0","360","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":255,"pos":[-640,2560],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":1221,"pos":[768,-1792],"params":["Float","False","Property","_EnableStarsRotation","Enable Stars Rotation","27","0","Create","True","0","0","0","False","1","Space(10)","False","","0","0","0","True","","Toggle","2","Key0","Key1","Create","True","True","All","False","False","True","9","1","FLOAT3","0,0,0","False","0","FLOAT3","0,0,0","False","2","FLOAT3","0,0,0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT3","0,0,0","False","6","FLOAT3","0,0,0","False","7","FLOAT3","0,0,0","False","8","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.DotProductOpNode, AmplifyShaderEditor","id":732,"pos":[2768,-1792],"params":["Inherit","False","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1167,"pos":[3056,-1056],"params":["Half","False","Constant","_Float0","Float 0","47","0","Create","True","0","0","0","False","0","False","Object","-1","","0.1","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1013,"pos":[1408,-1264],"params":["Half","False","Constant","_Float13","Float 13","47","0","Create","True","0","0","0","False","0","False","Object","-1","","1","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.TFHCRemapNode, AmplifyShaderEditor","id":537,"pos":[1408,-1536],"params":["Inherit","False","5","0","FLOAT","0","False","1","FLOAT","1","False","2","FLOAT","3","False","3","FLOAT","3","False","4","FLOAT","1","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.AbsOpNode, AmplifyShaderEditor","id":828,"pos":[3072,-1152],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1012,"pos":[1408,-1344],"params":["Half","False","Constant","_Float12","Float 12","47","0","Create","True","0","0","0","False","0","False","Object","-1","","2","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1017,"pos":[2816,-1040],"params":["Half","False","Constant","_Float17","Float 17","47","0","Create","True","0","0","0","False","0","False","Object","-1","","0","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1018,"pos":[2816,-960],"params":["Half","False","Constant","_Float18","Float 18","47","0","Create","True","0","0","0","False","0","False","Object","-1","","1","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.NegateNode, AmplifyShaderEditor","id":740,"pos":[3200,-1472],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":831,"pos":[3024,-960],"params":["Half","False","Property","_StarsHeightMask","Stars Height Mask","22","0","Create","True","0","0","0","False","0","False","Object","-1","","0","0","0","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.DynamicAppendNode, AmplifyShaderEditor","id":970,"pos":[160,-128],"params":["Inherit","False","FLOAT2","4","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","1","FLOAT2","0"]}
{"type":"AmplifyShaderEditor.PowerNode, AmplifyShaderEditor","id":554,"pos":[640,-768],"params":["Inherit","False","True","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.DotProductOpNode, AmplifyShaderEditor","id":1036,"pos":[0,1024],"params":["Inherit","False","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.DotProductOpNode, AmplifyShaderEditor","id":1067,"pos":[0,896],"params":["Inherit","False","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":971,"pos":[320,0],"params":["Inherit","False","300","CAMERA_MODE","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleAddOpNode, AmplifyShaderEditor","id":276,"pos":[-512,2464],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1241,"pos":[-896,-3712],"params":["Inherit","False","1239","VertexPos","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":212,"pos":[-384,-3264],"params":["Half","False","Property","_EquatorHeight","Equator Height","6","0","Create","True","0","0","0","False","0","False","Object","-1","","0.5","0.293","0","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.AbsOpNode, AmplifyShaderEditor","id":287,"pos":[-192,-3680],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.VertexToFragmentNode, AmplifyShaderEditor","id":1220,"pos":[1152,-1792],"params":["Inherit","False","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1246,"pos":[-896,2048],"params":["Inherit","False","1239","VertexPos","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.StepOpNode, AmplifyShaderEditor","id":533,"pos":[1664,-1536],"params":["Inherit","False","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StepOpNode, AmplifyShaderEditor","id":534,"pos":[1664,-1408],"params":["Inherit","False","2","0","FLOAT","3","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1127,"pos":[-896,2192],"params":["Inherit","False","300","CAMERA_MODE","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.TFHCRemapNode, AmplifyShaderEditor","id":832,"pos":[3328,-1152],"params":["Inherit","False","5","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","1","False","3","FLOAT","0","False","4","FLOAT","1","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.BreakToComponentsNode, AmplifyShaderEditor","id":972,"pos":[320,-128],"params":["Inherit","False","FLOAT2","1","0","FLOAT2","0,0","False","16","FLOAT","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT","5","FLOAT","6","FLOAT","7","FLOAT","8","FLOAT","9","FLOAT","10","FLOAT","11","FLOAT","12","FLOAT","13","FLOAT","14","FLOAT","15"]}
{"type":"AmplifyShaderEditor.FunctionNode, AmplifyShaderEditor","id":1412,"pos":[3328,-1792],"params":["Inherit","False","Remap To 0-1","-1","","29","d6eb10c035ec242479a0519a8ea4fd0f","0","3","6","FLOAT","0","False","7","FLOAT","-1","False","8","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1224,"pos":[1024,-896],"params":["Half","False","Constant","_Float1","Float 1","57","0","Create","True","0","0","0","False","0","False","Object","-1","","1","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SaturateNode, AmplifyShaderEditor","id":1441,"pos":[1024,-768],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.DynamicAppendNode, AmplifyShaderEditor","id":1066,"pos":[160,896],"params":["Inherit","False","FLOAT2","4","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","1","FLOAT2","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":976,"pos":[640,-32],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":975,"pos":[0,128],"params":["Half","False","Property","_SunSize","Sun Size","35","0","Create","True","0","0","0","False","0","False","Object","-1","","0.5","1","0.1","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1065,"pos":[320,1024],"params":["Inherit","False","300","CAMERA_MODE","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.Vector3Node, AmplifyShaderEditor","id":1376,"pos":[-896,2304],"params":["Inherit","False","Property","_CloudsRotationAxis","Clouds Rotation Axis","55","0","Create","True","0","0","0","False","1","StyledVector(18)","False","Object","-1","","0,1,0","0,1,0","0","4","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3"]}
{"type":"AmplifyShaderEditor.RadiansOpNode, AmplifyShaderEditor","id":47,"pos":[-384,2464],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.BreakToComponentsNode, AmplifyShaderEditor","id":1242,"pos":[-720,-3712],"params":["Inherit","False","FLOAT3","1","0","FLOAT3","0,0,0","False","16","FLOAT","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT","5","FLOAT","6","FLOAT","7","FLOAT","8","FLOAT","9","FLOAT","10","FLOAT","11","FLOAT","12","FLOAT","13","FLOAT","14","FLOAT","15"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":471,"pos":[-384,-3136],"params":["Half","False","Property","_EquatorSmoothness","Equator Smoothness","7","0","Create","True","0","0","0","False","0","False","Object","-1","","0.5","0.01","0.01","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.FunctionNode, AmplifyShaderEditor","id":1411,"pos":[0,-3328],"params":["Inherit","False","Remap To 0-1","-1","","30","d6eb10c035ec242479a0519a8ea4fd0f","0","3","6","FLOAT","0","False","7","FLOAT","0","False","8","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SamplerNode, AmplifyShaderEditor","id":564,"pos":[1408,-1792],"params":["Inherit","True","Property","_StarsCubemap","Stars Cubemap","16","1","[NoScaleOffset]","Create","True","0","0","0","False","2","Space(10)","StyledTextureSingleLine","False","","-1","None","4136c4d5d7422cf49a21b885d09f4518","True","0","False","white","LockedToCube","False","Object","-1","Auto","Cube","False","8","0","SAMPLERCUBE","","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT","1","False","6","FLOAT","0","False","7","SAMPLERSTATE","","False","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.BreakToComponentsNode, AmplifyShaderEditor","id":1247,"pos":[-736,2048],"params":["Inherit","False","FLOAT3","1","0","FLOAT3","0,0,0","False","16","FLOAT","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT","5","FLOAT","6","FLOAT","7","FLOAT","8","FLOAT","9","FLOAT","10","FLOAT","11","FLOAT","12","FLOAT","13","FLOAT","14","FLOAT","15"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":529,"pos":[1856,-1664],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":619,"pos":[1408,-1152],"params":["Half","False","Property","_StarsSize","Stars Size","20","0","Create","True","0","0","0","False","0","False","Object","-1","","0.5","0.99","0","0.99","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":530,"pos":[1856,-1536],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SaturateNode, AmplifyShaderEditor","id":724,"pos":[3504,-1792],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SaturateNode, AmplifyShaderEditor","id":822,"pos":[3520,-1152],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1128,"pos":[-640,2176],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":878,"pos":[1408,-896],"params":["Float","False","Property","_EnableStarsTwinkling","Enable Stars Twinkling","24","0","Create","True","0","0","0","False","1","Space(10)","False","","0","0","0","True","","Toggle","2","Key0","Key1","Create","True","True","All","False","False","True","9","1","FLOAT","0","False","0","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","4","FLOAT","0","False","5","FLOAT","0","False","6","FLOAT","0","False","7","FLOAT","0","False","8","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StepOpNode, AmplifyShaderEditor","id":1271,"pos":[-480,-1664],"params":["Inherit","False","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.BreakToComponentsNode, AmplifyShaderEditor","id":1063,"pos":[320,896],"params":["Inherit","False","FLOAT2","1","0","FLOAT2","0,0","False","16","FLOAT","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT","5","FLOAT","6","FLOAT","7","FLOAT","8","FLOAT","9","FLOAT","10","FLOAT","11","FLOAT","12","FLOAT","13","FLOAT","14","FLOAT","15"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":1272,"pos":[640,128],"params":["Inherit","False","3","0","FLOAT","20","False","1","FLOAT","2","False","2","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.DynamicAppendNode, AmplifyShaderEditor","id":980,"pos":[832,-128],"params":["Inherit","False","FLOAT2","4","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","1","FLOAT2","0"]}
{"type":"AmplifyShaderEditor.FunctionNode, AmplifyShaderEditor","id":1377,"pos":[-256,2304],"params":["Inherit","False","Compute Rotation","-1","","31","d0fbe60918b2bc146b660122744f3d02","0","4","38","FLOAT3","0,0,0","False","72","FLOAT3","0,0,0","False","41","FLOAT3","0,0,0","False","43","FLOAT","1","False","1","FLOAT3","19"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1046,"pos":[256,1280],"params":["Half","False","Property","_MoonSize","Moon Size","43","0","Create","True","0","0","0","False","0","False","Object","-1","","0.5","1","0.1","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1072,"pos":[640,992],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.NegateNode, AmplifyShaderEditor","id":1205,"pos":[-256,2464],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SaturateNode, AmplifyShaderEditor","id":208,"pos":[256,-3328],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StepOpNode, AmplifyShaderEditor","id":1270,"pos":[-512,-3200],"params":["Inherit","False","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.OneMinusNode, AmplifyShaderEditor","id":475,"pos":[-64,-3136],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1243,"pos":[-896,-2560],"params":["Inherit","False","1239","VertexPos","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":947,"pos":[3712,-1744],"params":["Float","False","Property","_ENABLESTARS_ON","Enable Stars","15","0","Create","False","0","0","0","False","0","False","","0","0","0","False","","Toggle","2","Key0","Key1","Reference","918","False","True","All","False","False","True","9","1","FLOAT","0","False","0","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","4","FLOAT","0","False","5","FLOAT","0","False","6","FLOAT","0","False","7","FLOAT","0","False","8","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":1250,"pos":[-256,-1664],"params":["Half","False","Starts_Bottom_Mask","-1","True","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":953,"pos":[3712,-1104],"params":["Float","False","Property","_ENABLESTARS_ON","Enable Stars","15","0","Create","False","0","0","0","False","0","False","","0","0","0","False","","Toggle","2","Key0","Key1","Reference","918","False","True","All","False","False","True","9","1","FLOAT","0","False","0","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","4","FLOAT","0","False","5","FLOAT","0","False","6","FLOAT","0","False","7","FLOAT","0","False","8","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1223,"pos":[1791.023,-1013.223],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.DynamicAppendNode, AmplifyShaderEditor","id":1129,"pos":[-512,2048],"params":["Inherit","False","FLOAT3","4","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.SimpleAddOpNode, AmplifyShaderEditor","id":527,"pos":[2048,-1792],"params":["Inherit","False","3","3","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":985,"pos":[1024,-128],"params":["Inherit","False","2","2","0","FLOAT2","0,0","False","1","FLOAT","5","False","1","FLOAT2","0"]}
{"type":"AmplifyShaderEditor.PosVertexDataNode, AmplifyShaderEditor","id":1174,"pos":[2816,384],"params":["Inherit","False","0","0","5","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1028,"pos":[2816,256],"params":["Inherit","False","1005","GlobalSunDirection","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.DynamicAppendNode, AmplifyShaderEditor","id":1069,"pos":[832,896],"params":["Inherit","False","FLOAT2","4","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","1","FLOAT2","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":1285,"pos":[640,1152],"params":["Inherit","False","3","0","FLOAT","20","False","1","FLOAT","2","False","2","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.FunctionNode, AmplifyShaderEditor","id":1378,"pos":[0,2304],"params":["Inherit","False","Compute Rotation Y","-1","","32","848a7021c0b62984d9ca063f51e19d69","0","2","38","FLOAT3","0,0,0","False","43","FLOAT","0","False","1","FLOAT3","19"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1384,"pos":[-896,-4864],"params":["Inherit","False","1239","VertexPos","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RelayNode, AmplifyShaderEditor","id":303,"pos":[896,-3216],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.PowerNode, AmplifyShaderEditor","id":470,"pos":[384,-3136],"params":["Inherit","False","False","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.ColorNode, AmplifyShaderEditor","id":194,"pos":[640,-3536],"params":["Half","False","Property","_GroundColor","Ground Color","5","0","Create","True","0","0","0","False","0","False","Object","-1","","0.4980392,0.497,0,1","0.4980389,0.497,0,1","False","True","0","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.ColorNode, AmplifyShaderEditor","id":182,"pos":[640,-3360],"params":["Half","False","Property","_SkyColor","Sky Color","3","0","Create","True","0","0","0","False","1","Space(10)","False","Object","-1","","0.4980392,0.7450981,1,1","0.4980389,0.7450981,1,1","False","True","0","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":283,"pos":[-640,-2368],"params":["Half","False","Property","_PatternContrast","Pattern Contrast","13","0","Create","True","0","0","0","False","1","Space(10)","False","Object","-1","","0.5","0.35","0","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SamplerNode, AmplifyShaderEditor","id":254,"pos":[-640,-2560],"params":["Inherit","True","Property","_PatternCubemap","Pattern Cubemap","12","1","[NoScaleOffset]","Create","True","0","0","0","False","2","Space(10)","StyledTextureSingleLine","False","","-1","None","2496987be02f21244bcd1e6c3bfa5aff","True","0","False","black","LockedToCube","False","Object","-1","Auto","Cube","False","8","0","SAMPLERCUBE","","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT","1","False","6","FLOAT","0","False","7","SAMPLERSTATE","","False","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.VertexToFragmentNode, AmplifyShaderEditor","id":856,"pos":[3968,-1088],"params":["Inherit","False","False","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleAddOpNode, AmplifyShaderEditor","id":626,"pos":[2176,-1392],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.VertexToFragmentNode, AmplifyShaderEditor","id":762,"pos":[3968,-1728],"params":["Inherit","False","False","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.DotProductOpNode, AmplifyShaderEditor","id":988,"pos":[3072,256],"params":["Inherit","False","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.ScaleAndOffsetNode, AmplifyShaderEditor","id":1273,"pos":[1280,-128],"params":["Inherit","False","3","0","FLOAT2","0,0","False","1","FLOAT","0.5","False","2","FLOAT","0.5","False","1","FLOAT2","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1075,"pos":[1024,896],"params":["Inherit","False","2","2","0","FLOAT2","0,0","False","1","FLOAT","5","False","1","FLOAT2","0"]}
{"type":"AmplifyShaderEditor.VertexToFragmentNode, AmplifyShaderEditor","id":1222,"pos":[256,2048],"params":["Inherit","False","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.VertexToFragmentNode, AmplifyShaderEditor","id":1207,"pos":[256,2304],"params":["Inherit","False","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.PosVertexDataNode, AmplifyShaderEditor","id":1176,"pos":[2816,1408],"params":["Inherit","False","0","0","5","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1056,"pos":[2816,1280],"params":["Inherit","False","1073","GlobalMoonDirection","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":241,"pos":[1024,2384],"params":["Half","False","Property","_CloudsHeight","Clouds Height","50","0","Create","True","0","0","0","False","1","Space(10)","False","Object","-1","","0","0","-0.5","0.5","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1090,"pos":[1024,2480],"params":["Half","False","Constant","_Float31","Float 31","53","0","Create","True","0","0","0","False","0","False","Object","-1","","-1","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1253,"pos":[4736,-1152],"params":["Inherit","False","1250","Starts_Bottom_Mask","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SamplerNode, AmplifyShaderEditor","id":1191,"pos":[-640,-4864],"params":["Inherit","True","Property","_BackgroundCubemap","Background Cubemap","8","1","[NoScaleOffset]","Create","True","0","0","0","False","2","Space(10)","StyledTextureSingleLine","False","","-1","None","None","True","0","False","black","LockedToCube","False","Object","-1","Auto","Cube","False","8","0","SAMPLERCUBE","","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT","1","False","6","FLOAT","0","False","7","SAMPLERSTATE","","False","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.ColorNode, AmplifyShaderEditor","id":181,"pos":[640,-3712],"params":["Half","False","Property","_EquatorColor","Equator Color","4","0","Create","True","0","0","0","False","1","","False","Object","-1","","1,0.747,0,1","1,0.747,0,1","False","True","0","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1196,"pos":[-640,-4672],"params":["Half","False","Property","_BackgroundExposure","Background Exposure","9","0","Create","True","0","0","0","False","1","Space(10)","False","Object","-1","","1","1","0","8","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RelayNode, AmplifyShaderEditor","id":417,"pos":[1152,-3136],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":180,"pos":[1152,-3536],"params":["Inherit","False","3","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":1380,"pos":[-256.824,-2559.056],"params":["Inherit","False","3","0","FLOAT","0.5","False","1","FLOAT","0","False","2","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SaturateNode, AmplifyShaderEditor","id":994,"pos":[3232,256],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":940,"pos":[1504.37,-122.7181],"params":["Float","False","Property","_ENABLESUN_ON","Enable Sun","32","0","Create","False","0","0","0","False","0","False","","0","0","0","False","","Toggle","2","Key0","Key1","Reference","919","False","True","All","False","False","True","9","1","FLOAT2","0,0","False","0","FLOAT2","0,0","False","2","FLOAT2","0,0","False","3","FLOAT2","0,0","False","4","FLOAT2","0,0","False","5","FLOAT2","0,0","False","6","FLOAT2","0,0","False","7","FLOAT2","0,0","False","8","FLOAT2","0,0","False","1","FLOAT2","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":1164,"pos":[640,2240],"params":["Float","False","Property","_EnableCloudsRotation","Enable Clouds Rotation","53","0","Create","True","0","0","0","False","1","Space(10)","False","","0","0","0","True","","Toggle","2","Key0","Key1","Create","True","True","All","False","False","True","9","1","FLOAT3","0,0,0","False","0","FLOAT3","0,0,0","False","2","FLOAT3","0,0,0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT3","0,0,0","False","6","FLOAT3","0,0,0","False","7","FLOAT3","0,0,0","False","8","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.DotProductOpNode, AmplifyShaderEditor","id":1054,"pos":[3072,1280],"params":["Inherit","False","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.ScaleAndOffsetNode, AmplifyShaderEditor","id":1286,"pos":[1280,896],"params":["Inherit","False","3","0","FLOAT2","0,0","False","1","FLOAT","0.5","False","2","FLOAT","0.5","False","1","FLOAT2","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":278,"pos":[1344,2464],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1248,"pos":[-896,3200],"params":["Inherit","False","1239","VertexPos","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":876,"pos":[4352,-1408],"params":["Inherit","False","3","3","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1251,"pos":[4736,-1088],"params":["Half","False","Property","_StarsBottomMask","Stars Bottom Mask","23","0","Create","True","0","0","0","False","1","StyledToggle","False","Object","-1","","0","1","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SaturateNode, AmplifyShaderEditor","id":1259,"pos":[4992,-1152],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":288,"pos":[1536,-3712],"params":["Inherit","False","3","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1197,"pos":[-128,-4864],"params":["Inherit","False","2","2","0","FLOAT3","0,0,0","False","1","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.SaturateNode, AmplifyShaderEditor","id":517,"pos":[-64,-2560],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":996,"pos":[2176,320],"params":["Half","False","Property","_SunIntensity","Sun Intensity","36","0","Create","True","0","0","0","False","0","False","Object","-1","","1","0.84","0","10","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":1027,"pos":[3424,299],"params":["Float","False","Property","_ENABLESUN_ON","Enable Sun","32","0","Create","False","0","0","0","False","0","False","","0","0","0","False","","Toggle","2","Key0","Key1","Reference","919","False","True","All","False","False","True","9","1","FLOAT","0","False","0","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","4","FLOAT","0","False","5","FLOAT","0","False","6","FLOAT","0","False","7","FLOAT","0","False","8","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.VertexToFragmentNode, AmplifyShaderEditor","id":993,"pos":[1808,-64],"params":["Inherit","False","False","False","1","0","FLOAT2","0,0","False","1","FLOAT2","0"]}
{"type":"AmplifyShaderEditor.SaturateNode, AmplifyShaderEditor","id":1055,"pos":[3232,1280],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.BreakToComponentsNode, AmplifyShaderEditor","id":245,"pos":[1024,2240],"params":["Inherit","False","FLOAT3","1","0","FLOAT3","0,0,0","False","16","FLOAT","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT","5","FLOAT","6","FLOAT","7","FLOAT","8","FLOAT","9","FLOAT","10","FLOAT","11","FLOAT","12","FLOAT","13","FLOAT","14","FLOAT","15"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":1057,"pos":[1504,897],"params":["Float","False","Property","_ENABLEMOON_ON","Enable Moon","40","0","Create","False","0","0","0","False","0","False","","0","0","0","False","","Toggle","2","Key0","Key1","Reference","1113","False","True","All","False","False","True","9","1","FLOAT2","0,0","False","0","FLOAT2","0,0","False","2","FLOAT2","0,0","False","3","FLOAT2","0,0","False","4","FLOAT2","0,0","False","5","FLOAT2","0,0","False","6","FLOAT2","0,0","False","7","FLOAT2","0,0","False","8","FLOAT2","0,0","False","1","FLOAT2","0"]}
{"type":"AmplifyShaderEditor.SimpleAddOpNode, AmplifyShaderEditor","id":244,"pos":[1488,2368],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.BreakToComponentsNode, AmplifyShaderEditor","id":1249,"pos":[-736,3200],"params":["Inherit","False","FLOAT3","1","0","FLOAT3","0,0,0","False","16","FLOAT","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT","5","FLOAT","6","FLOAT","7","FLOAT","8","FLOAT","9","FLOAT","10","FLOAT","11","FLOAT","12","FLOAT","13","FLOAT","14","FLOAT","15"]}
{"type":"AmplifyShaderEditor.ColorNode, AmplifyShaderEditor","id":1454,"pos":[4736,-1792],"params":["Half","False","Property","_StarsColor","Stars Color","19","0","Create","True","0","0","0","False","0","False","Object","-1","","1,1,1,1","0,0,0,0","True","True","0","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":629,"pos":[4736,-1568],"params":["Half","False","Property","_StarsIntensity","Stars Intensity","17","0","Create","True","0","0","0","False","1","Space(10)","False","Object","-1","","2","5","0","5","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.FloorOpNode, AmplifyShaderEditor","id":886,"pos":[4736,-1408],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SaturateNode, AmplifyShaderEditor","id":1458,"pos":[5120,-1344],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":1255,"pos":[5120,-1152],"params":["Inherit","False","3","0","FLOAT","1","False","1","FLOAT","1","False","2","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":218,"pos":[1792,-3712],"params":["Half","False","SKY","-1","True","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":1195,"pos":[256,-4864],"params":["Half","False","BACKGROUND","-1","True","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":513,"pos":[256,-2560],"params":["Half","False","PATTERN","-1","True","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SaturateNode, AmplifyShaderEditor","id":1281,"pos":[2544,512],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.VertexToFragmentNode, AmplifyShaderEditor","id":997,"pos":[3680,320],"params":["Inherit","False","False","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SamplerNode, AmplifyShaderEditor","id":995,"pos":[2176,-128],"params":["Inherit","True","Property","_SunTexture","Sun Texture","33","1","[NoScaleOffset]","Create","True","0","0","0","False","2","Space(10)","StyledTextureSingleLine","False","","-1","None","dd566660fc358aa4098080e42ca0db56","True","0","False","black","Auto","False","Object","-1","Auto","Texture2D","False","8","0","SAMPLER2D","","False","1","FLOAT2","0,0","False","2","FLOAT","0","False","3","FLOAT2","0,0","False","4","FLOAT2","0,0","False","5","FLOAT","1","False","6","FLOAT","0","False","7","SAMPLERSTATE","","False","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.ColorNode, AmplifyShaderEditor","id":998,"pos":[2176,96],"params":["Half","False","Property","_SunColor","Sun Color","34","0","Create","True","0","0","0","False","1","Space(10)","False","Object","-1","","1,1,1,1","1,1,1,1","False","True","0","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.VertexToFragmentNode, AmplifyShaderEditor","id":1043,"pos":[1793,898],"params":["Inherit","False","False","False","1","0","FLOAT2","0,0","False","1","FLOAT2","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1045,"pos":[2176,1408],"params":["Half","False","Property","_MoonIntensity","Moon Intensity","44","0","Create","True","0","0","0","False","0","False","Object","-1","","1","0.33","0","10","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":1052,"pos":[3424,1328],"params":["Float","False","Property","_ENABLEMOON_ON","Enable Moon","40","0","Create","False","0","0","0","False","0","False","","0","0","0","False","","Toggle","2","Key0","Key1","Reference","1113","False","True","All","False","False","True","9","1","FLOAT","0","False","0","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","4","FLOAT","0","False","5","FLOAT","0","False","6","FLOAT","0","False","7","FLOAT","0","False","8","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.DynamicAppendNode, AmplifyShaderEditor","id":246,"pos":[1664,2240],"params":["Inherit","False","FLOAT3","4","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.AbsOpNode, AmplifyShaderEditor","id":314,"pos":[-512,3200],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":325,"pos":[-896,3520],"params":["Half","False","Property","_FogSmoothness","Fog Smoothness","61","0","Create","True","0","0","0","False","0","False","Object","-1","","0","0","0.01","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":313,"pos":[-896,3392],"params":["Half","False","Property","_FogHeight","Fog Height","60","0","Create","True","0","0","0","False","1","Space(10)","False","Object","-1","","0","0","0","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMaxOpNode, AmplifyShaderEditor","id":1282,"pos":[2560,320],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","1","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1452,"pos":[5376,-1792],"params":["Inherit","False","2","2","0","FLOAT3","0,0,0","False","1","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":882,"pos":[5376,-1408],"params":["Inherit","False","3","3","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1199,"pos":[-896,-6528],"params":["Inherit","False","1195","BACKGROUND","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":220,"pos":[-896,-6656],"params":["Inherit","False","218","SKY","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1201,"pos":[-672,-6528],"params":["Inherit","False","2","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":516,"pos":[-384,-6912],"params":["Inherit","False","513","PATTERN","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1002,"pos":[2688,-128],"params":["Inherit","False","3","3","0","FLOAT","0","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1001,"pos":[4096,-32],"params":["Inherit","False","3","3","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SaturateNode, AmplifyShaderEditor","id":1283,"pos":[2688,1072],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMaxOpNode, AmplifyShaderEditor","id":1284,"pos":[2560,1408],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","1","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.VertexToFragmentNode, AmplifyShaderEditor","id":1051,"pos":[3712,1344],"params":["Inherit","False","False","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.ColorNode, AmplifyShaderEditor","id":1048,"pos":[2176,1152],"params":["Half","False","Property","_MoonColor","Moon Color","42","0","Create","True","0","0","0","False","1","Space(10)","False","Object","-1","","1,1,1,1","1,1,1,1","False","True","0","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.SamplerNode, AmplifyShaderEditor","id":1049,"pos":[2176,896],"params":["Inherit","True","Property","_MoonTexture","Moon Texture","41","1","[NoScaleOffset]","Create","True","0","0","0","False","2","Space(10)","StyledTextureSingleLine","False","","-1","None","b15c2596790c6ce42980282a7e842f57","True","0","False","black","Auto","False","Object","-1","Auto","Texture2D","False","8","0","SAMPLER2D","","False","1","FLOAT2","0,0","False","2","FLOAT","0","False","3","FLOAT2","0,0","False","4","FLOAT2","0,0","False","5","FLOAT","1","False","6","FLOAT","0","False","7","SAMPLERSTATE","","False","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.SamplerNode, AmplifyShaderEditor","id":41,"pos":[1856,2240],"params":["Inherit","True","Property","_CloudsCubemap","Clouds Cubemap","49","1","[NoScaleOffset]","Create","True","0","0","0","False","2","Space(10)","StyledTextureSingleLine","False","","-1","None","5bf545dec997743419de1df4195b3a0d","True","0","False","black","LockedToCube","False","Object","-1","Auto","Cube","False","8","0","SAMPLERCUBE","","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT","1","False","6","FLOAT","0","False","7","SAMPLERSTATE","","False","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.OneMinusNode, AmplifyShaderEditor","id":329,"pos":[-256,3520],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.FunctionNode, AmplifyShaderEditor","id":1414,"pos":[-320,3200],"params":["Inherit","False","Remap To 0-1","-1","","33","d6eb10c035ec242479a0519a8ea4fd0f","0","3","6","FLOAT","0","False","7","FLOAT","0","False","8","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":1453,"pos":[5568,-1792],"params":["Half","False","STARS","-1","True","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":630,"pos":[5568,-1408],"params":["Half","False","STARS_MASK","-1","True","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":1200,"pos":[-512,-6656],"params":["Float","False","Property","_BackgroundMode","Background Mode","2","0","Create","True","0","0","0","False","0","False","","0","0","0","True","","KeywordEnum","3","Colors","Cubemap","Combined","Create","True","True","All","False","False","True","9","1","FLOAT3","0,0,0","False","0","FLOAT3","0,0,0","False","2","FLOAT3","0,0,0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT3","0,0,0","False","6","FLOAT3","0,0,0","False","7","FLOAT3","0,0,0","False","8","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.BlendOpsNode, AmplifyShaderEditor","id":574,"pos":[-192,-6912],"params":["Inherit","False","Overlay","True","3","0","FLOAT","0","False","1","FLOAT3","0,0,0","False","2","FLOAT","1","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":632,"pos":[128,-6592],"params":["Inherit","False","630","STARS_MASK","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1456,"pos":[128,-6688],"params":["Inherit","False","1453","STARS","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":1003,"pos":[4352,128],"params":["Half","False","SUN_MASK","-1","True","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":1004,"pos":[4352,-128],"params":["Half","False","SUN","-1","True","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1053,"pos":[4096,992],"params":["Inherit","False","3","3","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1059,"pos":[2688,896],"params":["Inherit","False","3","3","0","FLOAT","0","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.ColorNode, AmplifyShaderEditor","id":232,"pos":[2688,2048],"params":["Half","False","Property","_CloudsShadowColor","Clouds Shadow Color","52","0","Create","True","0","0","0","False","0","False","Object","-1","","0.4980392,0.7450981,1,1","1,0.1843134,0,1","False","True","0","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.ColorNode, AmplifyShaderEditor","id":261,"pos":[2688,2208],"params":["Half","False","Property","_CloudsLightColor","Clouds Light Color","51","0","Create","True","0","0","0","False","0","False","Object","-1","","1,1,1,1","1,1,1,1","False","True","0","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.RelayNode, AmplifyShaderEditor","id":407,"pos":[2688,2384],"params":["Inherit","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.PowerNode, AmplifyShaderEditor","id":677,"pos":[-64,3200],"params":["Inherit","False","False","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":524,"pos":[128,-6912],"params":["Float","False","Property","_EnablePatternOverlay","Enable Pattern Overlay","11","0","Create","True","0","0","0","False","0","False","","0","0","0","True","","Toggle","2","Key0","Key1","Create","True","True","All","False","False","True","9","1","FLOAT3","0,0,0","False","0","FLOAT3","0,0,0","False","2","FLOAT3","0,0,0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT3","0,0,0","False","6","FLOAT3","0,0,0","False","7","FLOAT3","0,0,0","False","8","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":1455,"pos":[464,-6784],"params":["Inherit","False","3","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1112,"pos":[640,-6688],"params":["Inherit","False","1004","SUN","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1111,"pos":[640,-6592],"params":["Inherit","False","1003","SUN_MASK","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":1077,"pos":[4352,896],"params":["Half","False","MOON","-1","True","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":1078,"pos":[4352,1152],"params":["Half","False","MOON_MASK","-1","True","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":101,"pos":[3072,2048],"params":["Inherit","False","3","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","2","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1110,"pos":[128,3392],"params":["Half","False","Constant","_Float41","Float 41","55","0","Create","True","0","0","0","False","0","False","Object","-1","","0","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SaturateNode, AmplifyShaderEditor","id":316,"pos":[128,3200],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":679,"pos":[128,3520],"params":["Half","False","Property","_FogFill","Fog Fill","62","0","Create","True","0","0","0","False","0","False","Object","-1","","0","0","0","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":918,"pos":[640,-6912],"params":["Float","False","Property","_EnableStars","Enable Stars","15","0","Create","True","0","0","0","False","0","False","","0","0","0","True","","Toggle","2","Key0","Key1","Create","True","True","All","False","False","True","9","1","FLOAT3","0,0,0","False","0","FLOAT3","0,0,0","False","2","FLOAT3","0,0,0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT3","0,0,0","False","6","FLOAT3","0,0,0","False","7","FLOAT3","0,0,0","False","8","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":176,"pos":[960,-6784],"params":["Inherit","False","3","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1115,"pos":[1152,-6688],"params":["Inherit","False","1077","MOON","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1116,"pos":[1152,-6592],"params":["Inherit","False","1078","MOON_MASK","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":1406,"pos":[2176,2432],"params":["Half","False","CLOUDS_MASK","-1","True","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":222,"pos":[3536,2048],"params":["Half","False","CLOUDS","-1","True","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":678,"pos":[384,3200],"params":["Inherit","False","3","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":919,"pos":[1152,-6912],"params":["Float","False","Property","_EnableSun","Enable Sun","32","0","Create","True","0","0","0","False","0","False","","0","0","0","True","","Toggle","2","Key0","Key1","Create","True","True","All","False","False","True","9","1","FLOAT3","0,0,0","False","0","FLOAT3","0,0,0","False","2","FLOAT3","0,0,0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT3","0,0,0","False","6","FLOAT3","0,0,0","False","7","FLOAT3","0,0,0","False","8","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":1114,"pos":[1472,-6784],"params":["Inherit","False","3","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":228,"pos":[1856,-6688],"params":["Inherit","False","222","CLOUDS","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":229,"pos":[1856,-6592],"params":["Inherit","False","1406","CLOUDS_MASK","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":359,"pos":[768,3200],"params":["Half","False","FOG_MASK","-1","True","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.FogAndAmbientColorsNode, AmplifyShaderEditor","id":312,"pos":[2688,-6688],"params":["Inherit","False","unity_FogColor","0","1","COLOR","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":1113,"pos":[1664,-6912],"params":["Float","False","Property","_EnableMoon","Enable Moon","40","0","Create","True","0","0","0","False","0","False","","0","0","0","True","","Toggle","2","Key0","Key1","Create","True","True","All","False","False","True","9","1","FLOAT3","0,0,0","False","0","FLOAT3","0,0,0","False","2","FLOAT3","0,0,0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT3","0,0,0","False","6","FLOAT3","0,0,0","False","7","FLOAT3","0,0,0","False","8","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":227,"pos":[2176,-6784],"params":["Inherit","False","3","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.SwizzleNode, AmplifyShaderEditor","id":1405,"pos":[2944,-6688],"params":["Inherit","False","FLOAT3","0","1","2","3","1","0","COLOR","0,0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":436,"pos":[2944,-6592],"params":["Inherit","False","359","FOG_MASK","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":1120,"pos":[2688,-6912],"params":["Float","False","Property","_EnableClouds","Enable Clouds","48","0","Create","True","0","0","0","False","0","False","","0","0","0","True","","Toggle","2","Key0","Key1","Create","True","True","All","False","False","True","9","1","FLOAT3","0,0,0","False","0","FLOAT3","0,0,0","False","2","FLOAT3","0,0,0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT3","0,0,0","False","6","FLOAT3","0,0,0","False","7","FLOAT3","0,0,0","False","8","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":317,"pos":[3200,-6784],"params":["Inherit","False","3","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.SimpleSubtractOpNode, AmplifyShaderEditor","id":1395,"pos":[305.1353,2392.873],"params":["Inherit","False","2","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1185,"pos":[-160,-7680],"params":["Half","False","Property","_SunCat","[ Sun Cat ]","31","0","Create","True","0","0","0","True","1","StyledCategory(Sun)","False","Object","-1","","1","1","1","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1198,"pos":[-736,-7680],"params":["Half","False","Property","_BackgroundCat","[ Background Cat ]","1","0","Create","True","0","0","0","True","1","StyledCategory(Background, 5, 10)","False","Object","-1","","1","1","1","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1396,"pos":[43.13525,2430.873],"params":["Inherit","False","1005","GlobalSunDirection","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1240,"pos":[496,-7680],"params":["Half","False","Property","_SkyboxCat","[ Skybox Cat ]","63","0","Create","True","0","0","0","True","1","StyledCategory(Skybox Settings)","False","Object","-1","","1","1","1","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1226,"pos":[-896,3648],"params":["Inherit","False","Property","_EnableFogMessage","EnableFogMessage","59","0","Create","True","0","0","0","True","1","StyledMessage(Info, The fog color is controlled by the fog color set in the Lighting panel., _EnableBuiltinFog, 1, 10, 0)","False","Object","-1","","0","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":921,"pos":[3456,-6912],"params":["Float","False","Property","_EnableBuiltinFog","Enable Fog","58","0","Create","False","0","0","0","False","0","False","","0","0","0","True","","Toggle","2","Key0","Key1","Create","True","True","All","False","False","True","9","1","FLOAT3","0,0,0","False","0","FLOAT3","0,0,0","False","2","FLOAT3","0,0,0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT3","0,0,0","False","6","FLOAT3","0,0,0","False","7","FLOAT3","0,0,0","False","8","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1188,"pos":[-512,-7680],"params":["Half","False","Property","_PatternCat","[ Pattern Cat ]","10","0","Create","True","0","0","0","True","1","StyledCategory(Pattern)","False","Object","-1","","1","1","1","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleAddOpNode, AmplifyShaderEditor","id":1394,"pos":[-75.42523,2091.813],"params":["Inherit","False","2","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1183,"pos":[160,-7680],"params":["Half","False","Property","_CloudsCat","[ Clouds Cat ]","47","0","Create","True","0","0","0","True","1","StyledCategory(Clouds)","False","Object","-1","","1","1","1","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1393,"pos":[-352.8311,1999.014],"params":["Inherit","False","1005","GlobalSunDirection","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1184,"pos":[-896,-7680],"params":["Half","False","Property","_Banner","< Banner >","0","0","Create","True","0","0","0","True","1","StyledBanner(Polyverse Skies Standard)","False","Object","-1","","1","1","1","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1186,"pos":[-336,-7680],"params":["Half","False","Property","_StarsCat","[ Stars Cat ]","14","0","Create","True","0","0","0","True","1","StyledCategory(Stars)","False","Object","-1","","1","1","1","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1181,"pos":[336,-7680],"params":["Half","False","Property","_FogCat","[ Fog Cat ]","57","0","Create","True","0","0","0","True","1","StyledCategory(Fog)","False","Object","-1","","1","1","1","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1189,"pos":[0,-7680],"params":["Half","False","Property","_MoonCat","[ Moon Cat ]","39","0","Create","True","0","0","0","True","1","StyledCategory(Moon)","False","Object","-1","","1","1","1","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.TemplateMultiPassMasterNode, AmplifyShaderEditor","id":1180,"pos":[3792,-6912],"params":["Float","False","True","-1","2","PolyverseSkiesAsset.MaterialGUI","100","12","BOXOPHOBIC/Polyverse Skies/Standard","0770190933193b94aaa3065e307002fa","True","Unlit","0","1","Unlit","8","False","True","0","1","False","","0","False","","0","1","False","","0","False","","True","0","False","","0","False","","False","False","False","False","False","False","False","False","False","True","0","False","","True","True","0","False","","False","True","True","True","True","True","0","False","","False","False","False","False","False","False","False","True","False","255","False","","255","False","","255","False","","7","False","","1","False","","1","False","","1","False","","7","False","","1","False","","1","False","","1","False","","True","True","2","False","","False","True","False","0","False","","0","False","","True","2","False","","True","3","RenderType=Background=RenderType","Queue=Background=Queue=0","PreviewType=Skybox","True","2","True","14","all","0","True","True","1","1","False","","0","False","","1","1","False","","0","False","","True","0","False","","0","False","","False","False","False","False","False","False","False","False","False","False","False","True","0","False","","False","True","True","True","True","True","0","False","","False","False","False","False","False","False","False","True","False","0","False","","255","False","","255","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","True","True","2","False","","True","0","False","","True","False","0","False","","0","False","","False","False","False","False","0","","0","0","Standard","10","Surface","0","0","  Keep Alpha","0","0","  Blend","0","0","Alpha Clipping","0","0","  Use Shadow Threshold","0","0","Cast Shadows","0","639270861512866674","Write Depth","0","0","  Conservative","0","0","Extra Pre Pass","0","0","Vertex Position","1","0","0","3","False","True","False","False","","False","0"]}
{"type":"AmplifyShaderEditor.TemplateMultiPassMasterNode, AmplifyShaderEditor","id":1459,"pos":[3792,-6912],"params":["Float","False","False","-1","3","AmplifyShaderEditor.MaterialInspector","0","1","New Amplify Shader","0770190933193b94aaa3065e307002fa","True","ExtraPrePass","0","0","ExtraPrePass","0","False","True","1","1","False","","0","False","","1","1","False","","0","False","","True","1","False","","1","False","","False","False","False","False","False","False","False","False","False","True","0","False","","False","True","0","False","","False","True","True","True","True","True","0","False","","False","False","False","False","False","False","False","True","False","0","False","","255","False","","255","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","False","True","1","False","","False","True","True","0","False","","0","False","","True","1","False","","True","1","RenderType=Opaque=RenderType","True","3","True","14","all","0","False","True","1","1","False","","0","False","","0","1","False","","0","False","","False","False","False","False","False","False","False","False","False","False","False","False","True","0","False","","False","True","True","True","True","True","0","False","","False","False","False","False","False","False","False","True","False","0","False","","255","False","","255","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","False","True","1","False","","True","3","False","","True","True","0","False","","0","False","","False","False","False","False","0","","0","0","Standard","0","True","0"]}
{"type":"AmplifyShaderEditor.TemplateMultiPassMasterNode, AmplifyShaderEditor","id":1460,"pos":[3792,-6902],"params":["Float","False","False","-1","3","AmplifyShaderEditor.MaterialInspector","0","1","New Amplify Shader","0770190933193b94aaa3065e307002fa","True","ShadowCaster","0","2","ShadowCaster","0","False","True","1","1","False","","0","False","","1","1","False","","0","False","","True","1","False","","1","False","","False","False","False","False","False","False","False","False","False","True","0","False","","False","True","0","False","","False","True","True","True","True","True","0","False","","False","False","False","False","False","False","False","True","False","0","False","","255","False","","255","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","False","True","1","False","","False","True","True","0","False","","0","False","","True","1","False","","True","1","RenderType=Opaque=RenderType","True","3","True","14","all","0","False","False","False","False","False","False","False","False","False","False","False","False","True","0","False","","False","False","False","False","False","False","False","False","False","False","False","False","False","True","1","False","","True","3","False","","False","False","True","1","LightMode=ShadowCaster","False","False","0","","0","0","Standard","0","True","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":1030,"pos":[-896,768],"params":["Inherit","False","2948.242","100","Calculate Moon Position","0","MOON","0,0.4980392,0,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":480,"pos":[640,-3840],"params":["Inherit","False","770.3374","100","Color Gradient Colors","0","","0,0.4980392,1,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":479,"pos":[-896,-3840],"params":["Inherit","False","1405.403","100","Color Gradient Calculation","0","SKY","0,0.4980392,0,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":952,"pos":[2432,-1280],"params":["Inherit","False","1793.068","100","Stars Horizon Height Mask","0","","1,0,0,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":1091,"pos":[1024,1920],"params":["Inherit","False","1347.811","100","Clouds Cubemap","0","","0,0.4980392,1,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":1031,"pos":[2176,768],"params":["Inherit","False","509.1301","100","Moon Texture, Color and Intensity","0","","0,0.4980392,1,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":1006,"pos":[-896,-256],"params":["Inherit","False","2942.402","100","Calculate Sun Position","0","SUN","0,0.4980392,0,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":1026,"pos":[2816,128],"params":["Inherit","False","1029.934","100","Direction Negative Z Mask","0","","1,0,0,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":1136,"pos":[-896,1920],"params":["Inherit","False","1789.754","100","Clouds Coordinates","0","CLOUDS","0,0.4980392,0,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":700,"pos":[-896,3072],"params":["Inherit","False","1411.941","100","Fog Coords on Screen","0","BUILT-IN FOG","0,0.4980392,0,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":1117,"pos":[-896,-7040],"params":["Inherit","False","4866.933","100","","0","FINAL COLOR","0.4980392,1,0,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":1187,"pos":[-896,-7808],"params":["Inherit","False","1558.748","100","Drawers","0","","1,0.252,0,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":949,"pos":[2432,-1920],"params":["Inherit","False","1751.366","100","Stars Sun Mask","0","","1,0,0,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":942,"pos":[-896,-1920],"params":["Inherit","False","3070.282","100","Stars Cubemap Coords and Cubemap RGB Layer","0","STARS","0,0.4980392,1,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":1035,"pos":[2816,1152],"params":["Inherit","False","1054.961","100","Direction Negative Z Mask","0","","1,0,0,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":639,"pos":[-896,-2688],"params":["Inherit","False","1047.013","100","Pattern Overlay Texture and Contrast","0","PATTERN OVERLAY","0,0.4980392,1,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":431,"pos":[-896,-6144],"params":["Inherit","False","770.2415","100","Switch between Perspective / Orthographic camera","0","CAMERA MODE","1,0,1,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":914,"pos":[2688,1920],"params":["Inherit","False","1028.475","100","Cloud Colors","0","","0,0.4980392,1,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":991,"pos":[2176,-256],"params":["Inherit","False","512.5464","100","Sun Texture, Color and Intensity","0","","0,0.4980392,1,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":955,"pos":[-896,-1024],"params":["Inherit","False","2050.233","100","Stars Twinkling ","0","","1,0,0,1","0","0"]}
{"wire":[1269,0,1265,0]}
{"wire":[309,0,267,2]}
{"wire":[309,1,267,1]}
{"wire":[1416,0,1415,1]}
{"wire":[1416,1,1415,2]}
{"wire":[1425,0,1426,1]}
{"wire":[1425,1,1426,3]}
{"wire":[1433,0,546,0]}
{"wire":[1438,0,546,0]}
{"wire":[1264,1,1269,0]}
{"wire":[268,0,1007,0]}
{"wire":[268,1,309,0]}
{"wire":[268,2,267,4]}
{"wire":[1446,0,1444,0]}
{"wire":[1210,0,1209,0]}
{"wire":[1210,1,1208,0]}
{"wire":[1421,0,1416,0]}
{"wire":[1417,0,1433,0]}
{"wire":[1437,0,1438,0]}
{"wire":[1424,0,1425,0]}
{"wire":[1447,0,1448,0]}
{"wire":[1237,0,1267,0]}
{"wire":[1293,0,1233,0]}
{"wire":[1262,0,1234,0]}
{"wire":[1262,1,1264,0]}
{"wire":[300,0,268,0]}
{"wire":[1443,0,938,0]}
{"wire":[1443,1,1446,0]}
{"wire":[1443,2,1445,0]}
{"wire":[1213,0,1211,0]}
{"wire":[1213,1,1210,0]}
{"wire":[1422,0,1421,0]}
{"wire":[1422,1,1417,0]}
{"wire":[1427,0,1437,0]}
{"wire":[1427,1,1424,0]}
{"wire":[1451,0,1061,0]}
{"wire":[1451,1,1447,0]}
{"wire":[1451,2,1449,0]}
{"wire":[1239,0,1237,0]}
{"wire":[1368,38,1262,0]}
{"wire":[1368,41,1238,0]}
{"wire":[1368,43,1293,0]}
{"wire":[1442,0,1443,0]}
{"wire":[1216,0,1213,0]}
{"wire":[962,0,1442,0]}
{"wire":[962,1,961,0]}
{"wire":[964,0,1005,0]}
{"wire":[964,1,962,0]}
{"wire":[1428,0,1427,0]}
{"wire":[1418,0,1422,0]}
{"wire":[1450,0,1451,0]}
{"wire":[1073,0,1450,0]}
{"wire":[1267,0,1368,19]}
{"wire":[1245,0,1244,0]}
{"wire":[1215,0,1245,1]}
{"wire":[1215,1,1212,0]}
{"wire":[1005,0,1442,0]}
{"wire":[1369,38,1217,0]}
{"wire":[1369,41,1228,0]}
{"wire":[1218,0,1216,0]}
{"wire":[1058,0,1450,0]}
{"wire":[1058,1,1062,0]}
{"wire":[965,0,964,0]}
{"wire":[967,0,962,0]}
{"wire":[1431,0,1418,0]}
{"wire":[1431,1,1428,0]}
{"wire":[1038,0,1073,0]}
{"wire":[1038,1,1058,0]}
{"wire":[1217,0,1245,0]}
{"wire":[1217,1,1215,0]}
{"wire":[1217,2,1245,2]}
{"wire":[1370,38,1369,19]}
{"wire":[1370,43,1218,0]}
{"wire":[969,0,965,0]}
{"wire":[969,1,1173,0]}
{"wire":[738,0,733,0]}
{"wire":[968,0,967,0]}
{"wire":[968,1,1173,0]}
{"wire":[1440,0,1431,0]}
{"wire":[1064,0,1038,0]}
{"wire":[1039,0,1058,0]}
{"wire":[255,0,701,0]}
{"wire":[255,1,260,0]}
{"wire":[1221,1,1217,0]}
{"wire":[1221,0,1370,19]}
{"wire":[732,0,1148,0]}
{"wire":[732,1,731,0]}
{"wire":[537,0,528,0]}
{"wire":[537,1,1010,0]}
{"wire":[537,2,1011,0]}
{"wire":[537,3,1011,0]}
{"wire":[537,4,1010,0]}
{"wire":[828,0,1172,2]}
{"wire":[740,0,738,0]}
{"wire":[970,0,968,0]}
{"wire":[970,1,969,0]}
{"wire":[554,0,1440,0]}
{"wire":[554,1,555,0]}
{"wire":[1036,0,1039,0]}
{"wire":[1036,1,1175,0]}
{"wire":[1067,0,1064,0]}
{"wire":[1067,1,1175,0]}
{"wire":[276,0,48,0]}
{"wire":[276,1,255,0]}
{"wire":[287,0,1242,1]}
{"wire":[1220,0,1221,0]}
{"wire":[533,0,537,0]}
{"wire":[533,1,1012,0]}
{"wire":[534,0,537,0]}
{"wire":[534,1,1013,0]}
{"wire":[832,0,828,0]}
{"wire":[832,1,1017,0]}
{"wire":[832,2,831,0]}
{"wire":[832,3,1167,0]}
{"wire":[832,4,1018,0]}
{"wire":[972,0,970,0]}
{"wire":[1412,6,732,0]}
{"wire":[1412,8,740,0]}
{"wire":[1441,0,554,0]}
{"wire":[1066,0,1036,0]}
{"wire":[1066,1,1067,0]}
{"wire":[976,0,972,1]}
{"wire":[976,1,971,0]}
{"wire":[47,0,276,0]}
{"wire":[1242,0,1241,0]}
{"wire":[1411,6,287,0]}
{"wire":[1411,8,212,0]}
{"wire":[564,1,1220,0]}
{"wire":[1247,0,1246,0]}
{"wire":[529,0,564,2]}
{"wire":[529,1,533,0]}
{"wire":[530,0,564,3]}
{"wire":[530,1,534,0]}
{"wire":[724,0,1412,0]}
{"wire":[822,0,832,0]}
{"wire":[1128,0,1247,1]}
{"wire":[1128,1,1127,0]}
{"wire":[878,1,1224,0]}
{"wire":[878,0,1441,0]}
{"wire":[1271,1,1215,0]}
{"wire":[1063,0,1066,0]}
{"wire":[1272,2,975,0]}
{"wire":[980,0,972,0]}
{"wire":[980,1,976,0]}
{"wire":[1377,38,1129,0]}
{"wire":[1377,41,1376,0]}
{"wire":[1072,0,1063,1]}
{"wire":[1072,1,1065,0]}
{"wire":[1205,0,47,0]}
{"wire":[208,0,1411,0]}
{"wire":[1270,1,1242,1]}
{"wire":[475,0,471,0]}
{"wire":[947,0,724,0]}
{"wire":[1250,0,1271,0]}
{"wire":[953,0,822,0]}
{"wire":[1223,0,619,0]}
{"wire":[1223,1,878,0]}
{"wire":[1129,0,1247,0]}
{"wire":[1129,1,1128,0]}
{"wire":[1129,2,1247,2]}
{"wire":[527,0,564,1]}
{"wire":[527,1,529,0]}
{"wire":[527,2,530,0]}
{"wire":[985,0,980,0]}
{"wire":[985,1,1272,0]}
{"wire":[1069,0,1063,0]}
{"wire":[1069,1,1072,0]}
{"wire":[1285,2,1046,0]}
{"wire":[1378,38,1377,19]}
{"wire":[1378,43,1205,0]}
{"wire":[303,0,1270,0]}
{"wire":[470,0,208,0]}
{"wire":[470,1,475,0]}
{"wire":[254,1,1243,0]}
{"wire":[856,0,953,0]}
{"wire":[626,0,527,0]}
{"wire":[626,1,1223,0]}
{"wire":[762,0,947,0]}
{"wire":[988,0,1028,0]}
{"wire":[988,1,1174,0]}
{"wire":[1273,0,985,0]}
{"wire":[1075,0,1069,0]}
{"wire":[1075,1,1285,0]}
{"wire":[1222,0,1129,0]}
{"wire":[1207,0,1378,19]}
{"wire":[1191,1,1384,0]}
{"wire":[417,0,470,0]}
{"wire":[180,0,194,5]}
{"wire":[180,1,182,5]}
{"wire":[180,2,303,0]}
{"wire":[1380,1,254,1]}
{"wire":[1380,2,283,0]}
{"wire":[994,0,988,0]}
{"wire":[940,0,1273,0]}
{"wire":[1164,1,1222,0]}
{"wire":[1164,0,1207,0]}
{"wire":[1054,0,1056,0]}
{"wire":[1054,1,1176,0]}
{"wire":[1286,0,1075,0]}
{"wire":[278,0,241,0]}
{"wire":[278,1,1090,0]}
{"wire":[876,0,762,0]}
{"wire":[876,1,626,0]}
{"wire":[876,2,856,0]}
{"wire":[1259,0,1253,0]}
{"wire":[288,0,181,5]}
{"wire":[288,1,180,0]}
{"wire":[288,2,417,0]}
{"wire":[1197,0,1191,5]}
{"wire":[1197,1,1196,0]}
{"wire":[517,0,1380,0]}
{"wire":[1027,0,994,0]}
{"wire":[993,0,940,0]}
{"wire":[1055,0,1054,0]}
{"wire":[245,0,1164,0]}
{"wire":[1057,0,1286,0]}
{"wire":[244,0,245,1]}
{"wire":[244,1,278,0]}
{"wire":[1249,0,1248,0]}
{"wire":[886,0,876,0]}
{"wire":[1458,0,629,0]}
{"wire":[1255,1,1259,0]}
{"wire":[1255,2,1251,0]}
{"wire":[218,0,288,0]}
{"wire":[1195,0,1197,0]}
{"wire":[513,0,517,0]}
{"wire":[1281,0,996,0]}
{"wire":[997,0,1027,0]}
{"wire":[995,1,993,0]}
{"wire":[1043,0,1057,0]}
{"wire":[1052,0,1055,0]}
{"wire":[246,0,245,0]}
{"wire":[246,1,244,0]}
{"wire":[246,2,245,2]}
{"wire":[314,0,1249,1]}
{"wire":[1282,0,996,0]}
{"wire":[1452,0,1454,5]}
{"wire":[1452,1,629,0]}
{"wire":[882,0,886,0]}
{"wire":[882,1,1458,0]}
{"wire":[882,2,1255,0]}
{"wire":[1201,0,220,0]}
{"wire":[1201,1,1199,0]}
{"wire":[1002,0,995,1]}
{"wire":[1002,1,998,5]}
{"wire":[1002,2,1282,0]}
{"wire":[1001,0,995,4]}
{"wire":[1001,1,1281,0]}
{"wire":[1001,2,997,0]}
{"wire":[1283,0,1045,0]}
{"wire":[1284,0,1045,0]}
{"wire":[1051,0,1052,0]}
{"wire":[1049,1,1043,0]}
{"wire":[41,1,246,0]}
{"wire":[329,0,325,0]}
{"wire":[1414,6,314,0]}
{"wire":[1414,8,313,0]}
{"wire":[1453,0,1452,0]}
{"wire":[630,0,882,0]}
{"wire":[1200,1,220,0]}
{"wire":[1200,0,1199,0]}
{"wire":[1200,2,1201,0]}
{"wire":[574,0,516,0]}
{"wire":[574,1,1200,0]}
{"wire":[1003,0,1001,0]}
{"wire":[1004,0,1002,0]}
{"wire":[1053,0,1049,4]}
{"wire":[1053,1,1051,0]}
{"wire":[1053,2,1283,0]}
{"wire":[1059,0,1049,1]}
{"wire":[1059,1,1048,5]}
{"wire":[1059,2,1284,0]}
{"wire":[407,0,41,5]}
{"wire":[677,0,1414,0]}
{"wire":[677,1,329,0]}
{"wire":[524,1,1200,0]}
{"wire":[524,0,574,0]}
{"wire":[1455,0,524,0]}
{"wire":[1455,1,1456,0]}
{"wire":[1455,2,632,0]}
{"wire":[1077,0,1059,0]}
{"wire":[1078,0,1053,0]}
{"wire":[101,0,232,5]}
{"wire":[101,1,261,5]}
{"wire":[101,2,407,0]}
{"wire":[316,0,677,0]}
{"wire":[918,1,524,0]}
{"wire":[918,0,1455,0]}
{"wire":[176,0,918,0]}
{"wire":[176,1,1112,0]}
{"wire":[176,2,1111,0]}
{"wire":[1406,0,41,4]}
{"wire":[222,0,101,0]}
{"wire":[678,0,316,0]}
{"wire":[678,1,1110,0]}
{"wire":[678,2,679,0]}
{"wire":[919,1,918,0]}
{"wire":[919,0,176,0]}
{"wire":[1114,0,919,0]}
{"wire":[1114,1,1115,0]}
{"wire":[1114,2,1116,0]}
{"wire":[359,0,678,0]}
{"wire":[1113,1,919,0]}
{"wire":[1113,0,1114,0]}
{"wire":[227,0,1113,0]}
{"wire":[227,1,228,0]}
{"wire":[227,2,229,0]}
{"wire":[1405,0,312,0]}
{"wire":[1120,1,1113,0]}
{"wire":[1120,0,227,0]}
{"wire":[317,0,1405,0]}
{"wire":[317,1,1120,0]}
{"wire":[317,2,436,0]}
{"wire":[1395,0,1378,19]}
{"wire":[1395,1,1396,0]}
{"wire":[921,1,1120,0]}
{"wire":[921,0,317,0]}
{"wire":[1394,0,1393,0]}
{"wire":[1394,1,1129,0]}
{"wire":[1180,0,921,0]}
ASEEND*/
//CHKSM=DA195103D7F5B507C42E9B63078C947F874F3F6A