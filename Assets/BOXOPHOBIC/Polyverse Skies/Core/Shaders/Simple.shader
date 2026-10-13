// Made with Amplify Shader Editor v1.9.9.13
// Available at the Unity Asset Store - http://u3d.as/y3X 
Shader "BOXOPHOBIC/Polyverse Skies/Simple"
{
	Properties
	{
		[StyledBanner(Polyverse Skies Simple)] _Banner( "< Banner >", Float ) = 1
		[StyledCategory(Background Settings, 5, 10)] _BackgroundCat( "[ Background Cat ]", Float ) = 1
		[KeywordEnum( Colors,Cubemap,Combined )] _BackgroundMode( "Background Mode", Float ) = 0
		[Space(10)] _SkyColor( "Sky Color", Color ) = ( 0.4980392, 0.7450981, 1, 1 )
		_EquatorColor( "Equator Color", Color ) = ( 1, 0.747, 0, 1 )
		_GroundColor( "Ground Color", Color ) = ( 0.4980392, 0.497, 0, 1 )
		_EquatorHeight( "Equator Height", Range( 0, 1 ) ) = 0.5
		_EquatorSmoothness( "Equator Smoothness", Range( 0.01, 1 ) ) = 0.5
		[NoScaleOffset][Space(10)][StyledTextureSingleLine] _BackgroundCubemap( "Background Cubemap", CUBE ) = "black" {}
		[Space(10)] _BackgroundExposure( "Background Exposure", Range( 0, 8 ) ) = 1
		[StyledCategory(Stars Settings)] _StarsCat( "[ Stars Cat ]", Float ) = 1
		[Toggle( _ENABLESTARS_ON )] _EnableStars( "Enable Stars", Float ) = 0
		[NoScaleOffset][Space(10)][StyledTextureSingleLine] _StarsCubemap( "Stars Cubemap", CUBE ) = "white" {}
		[Space(10)] _StarsSize( "Stars Size", Range( 0, 0.99 ) ) = 0.5
		_StarsColor( "Stars Color", Color ) = ( 1, 1, 1, 1 )
		_StarsIntensity( "Stars Intensity", Range( 0, 5 ) ) = 2
		_StarsHeightMask( "Stars Height Mask", Range( 0, 1 ) ) = 0
		[StyledToggle] _StarsBottomMask( "Stars Bottom Mask", Float ) = 0
		[StyledCategory(Sun Settings)] _SunCat( "[ Sun Cat ]", Float ) = 1
		[Toggle( _ENABLESUN_ON )] _EnableSun( "Enable Sun", Float ) = 0
		[NoScaleOffset][Space(10)][StyledTextureSingleLine] _SunTexture( "Sun Texture", 2D ) = "black" {}
		[Space(10)] _SunColor( "Sun Color", Color ) = ( 1, 1, 1, 1 )
		_SunSize( "Sun Size", Range( 0.1, 1 ) ) = 0.5
		_SunIntensity( "Sun Intensity", Range( 0, 10 ) ) = 1
		[Enum(Global From Script,0,Local From Material,1)] _SunDirectionMode( "Sun Direction", Float ) = 0
		[StyledVector(18)] _SunDirection( "Sun Direction", Vector ) = ( 0, 0, 1, 0 )
		[StyledCategory(Clouds Settings)] _CloudsCat( "[ Clouds Cat ]", Float ) = 1
		[Toggle( _ENABLECLOUDS_ON )] _EnableClouds( "Enable Clouds", Float ) = 0
		[NoScaleOffset][Space(10)][StyledTextureSingleLine] _CloudsCubemap( "Clouds Cubemap", CUBE ) = "black" {}
		[Space(10)] _CloudsIntensity( "Clouds Intensity", Range( 0, 1 ) ) = 1
		_CloudsHeight( "Clouds Height", Range( -0.5, 0.5 ) ) = 0
		_CloudsLightColor( "Clouds Light Color", Color ) = ( 1, 1, 1, 1 )
		_CloudsShadowColor( "Clouds Shadow Color", Color ) = ( 0.4980392, 0.7450981, 1, 1 )
		[StyledCategory(Fog Settings)] _FogCat( "[ Fog Cat ]", Float ) = 1
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
				#pragma shader_feature_local _ENABLESUN_ON
				#pragma shader_feature_local _ENABLESTARS_ON
				#pragma shader_feature_local _BACKGROUNDMODE_COLORS _BACKGROUNDMODE_CUBEMAP _BACKGROUNDMODE_COMBINED


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
					UNITY_VERTEX_INPUT_INSTANCE_ID
					UNITY_VERTEX_OUTPUT_STEREO
				};

				uniform half _SkyboxCat;
				uniform half _FogCat;
				uniform half _Banner;
				uniform float _EnableFogMessage;
				uniform half _SunCat;
				uniform half _BackgroundCat;
				uniform half _CloudsCat;
				uniform half _StarsCat;
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
				uniform half _StarsHeightMask;
				uniform half4 _StarsColor;
				uniform half _StarsIntensity;
				uniform samplerCUBE _StarsCubemap;
				uniform half _StarsSize;
				uniform half _StarsBottomMask;
				uniform half3 PolyverseSunDir;
				uniform half3 _SunDirection;
				uniform half _SunDirectionMode;
				uniform half _SunSize;
				uniform sampler2D _SunTexture;
				uniform half4 _SunColor;
				uniform half _SunIntensity;
				uniform half _CloudsHeight;
				uniform half4 _CloudsShadowColor;
				uniform half4 _CloudsLightColor;
				uniform samplerCUBE _CloudsCubemap;
				uniform half _CloudsIntensity;
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

					float3 normalizeResult46_g3 = normalize( _SkyboxRotationAxis );
					half3 RotationAxis42_g3 = normalizeResult46_g3;
					float3 appendResult1239 = (float3(0.0 , -_SkyboxOffset , 0.0));
					half3 VertexPos40_g3 = ( v.vertex.xyz + appendResult1239 );
					float dotResult48_g3 = dot( RotationAxis42_g3 , VertexPos40_g3 );
					float3 CPA50_g3 = ( RotationAxis42_g3 * dotResult48_g3 );
					half Angle44_g3 = radians( _SkyboxRotation );
					float3 normalizeResult1241 = normalize( ( CPA50_g3 + ( ( VertexPos40_g3 - CPA50_g3 ) * cos( Angle44_g3 ) ) + ( cross( RotationAxis42_g3 , ( VertexPos40_g3 - CPA50_g3 ) ) * sin( Angle44_g3 ) ) ) );
					float3 vertexToFrag1216 = normalizeResult1241;
					o.ase_texcoord.xyz = vertexToFrag1216;
					float staticSwitch1166 = ( float )0;
					#ifdef _ENABLESTARS_ON
					{
						staticSwitch1166 = saturate(  (0.1 + ( abs( v.vertex.xyz.y ) - 0.0 ) * ( 1.0 - 0.1 ) / ( _StarsHeightMask - 0.0 ) ) );
					}
					#else
					{
						staticSwitch1166 = 0.0;
					}
					#endif
					float vertexToFrag856 = staticSwitch1166;
					o.ase_texcoord.w = vertexToFrag856;
					half3 VertexPos1217 = vertexToFrag1216;
					float3 break1223 = VertexPos1217;
					float lerpResult268 = lerp( 1.0 , ( unity_OrthoParams.y / unity_OrthoParams.x ) , unity_OrthoParams.w);
					half CAMERA_MODE300 = lerpResult268;
					float temp_output_673_0 = ( break1223.y * CAMERA_MODE300 );
					float3 staticSwitch1165 = ( float3 )0;
					#ifdef _ENABLESTARS_ON
					{
						float3 appendResult675 = (float3(break1223.x , temp_output_673_0 , break1223.z));
						staticSwitch1165 = appendResult675;
					}
					#else
					{
						staticSwitch1165 = float3( 0,0,0 );
					}
					#endif
					float3 vertexToFrag763 = staticSwitch1165;
					o.ase_texcoord2.xyz = vertexToFrag763;
					float3 normalizeResult1258 = normalize( _SunDirection );
					float3 lerpResult1256 = lerp( PolyverseSunDir , normalizeResult1258 , _SunDirectionMode);
					half3 GlobalSunDirection1005 = -lerpResult1256;
					float2 staticSwitch1168 = ( float2 )0;
					#ifdef _ENABLESUN_ON
					{
						float3 temp_output_962_0 = cross( -lerpResult1256 , half3( 0, 1, 0 ) );
						float3 normalizeResult967 = normalize( temp_output_962_0 );
						float dotResult968 = dot( normalizeResult967 , v.vertex.xyz );
						float3 normalizeResult965 = normalize( cross( GlobalSunDirection1005 , temp_output_962_0 ) );
						float dotResult969 = dot( normalizeResult965 , v.vertex.xyz );
						float2 appendResult970 = (float2(dotResult968 , dotResult969));
						float2 break972 = appendResult970;
						float2 appendResult980 = (float2(break972.x , ( break972.y * CAMERA_MODE300 )));
						float lerpResult1246 = lerp( 20.0 , 2.0 , _SunSize);
						staticSwitch1168 = (( appendResult980 * lerpResult1246 )*0.5 + 0.5);
					}
					#else
					{
						staticSwitch1168 = float2( 0,0 );
					}
					#endif
					float2 vertexToFrag993 = staticSwitch1168;
					o.ase_texcoord3.xy = vertexToFrag993;
					float staticSwitch1169 = ( float )0;
					#ifdef _ENABLESUN_ON
					{
						float dotResult988 = dot( GlobalSunDirection1005 , v.vertex.xyz );
						staticSwitch1169 = saturate( dotResult988 );
					}
					#else
					{
						staticSwitch1169 = 0.0;
					}
					#endif
					float vertexToFrag997 = staticSwitch1169;
					o.ase_texcoord2.w = vertexToFrag997;
					float3 staticSwitch1163 = ( float3 )0;
					#ifdef _ENABLECLOUDS_ON
					{
						float3 break1225 = VertexPos1217;
						float3 appendResult246 = (float3(break1225.x , ( ( break1225.y + ( _CloudsHeight * -1.0 ) ) * CAMERA_MODE300 ) , break1225.z));
						staticSwitch1163 = appendResult246;
					}
					#else
					{
						staticSwitch1163 = float3( 0,0,0 );
					}
					#endif
					float3 vertexToFrag1133 = staticSwitch1163;
					o.ase_texcoord4.xyz = vertexToFrag1133;
					
					o.ase_texcoord1 = v.vertex;
					
					//setting value to unused interpolator channels and avoid initialization warnings
					o.ase_texcoord3.zw = 0;
					o.ase_texcoord4.w = 0;

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

					float3 vertexToFrag1216 = IN.ase_texcoord.xyz;
					half3 VertexPos1217 = vertexToFrag1216;
					float3 lerpResult180 = lerp( _GroundColor.rgb , _SkyColor.rgb , step( 0.0 , VertexPos1217.y ));
					float temp_output_7_0_g7 = 0.0;
					float saferPower470 = abs( saturate( ( ( abs( VertexPos1217.y ) - temp_output_7_0_g7 ) / ( _EquatorHeight - temp_output_7_0_g7 ) ) ) );
					float3 lerpResult288 = lerp( _EquatorColor.rgb , lerpResult180 , pow( saferPower470 , ( 1.0 - _EquatorSmoothness ) ));
					half3 SKY218 = lerpResult288;
					half3 BACKGROUND1202 = ( texCUBE( _BackgroundCubemap, VertexPos1217 ).rgb * _BackgroundExposure );
					float3 staticSwitch1207 = ( float3 )0;
					#if defined( _BACKGROUNDMODE_COLORS )
					{
						staticSwitch1207 = SKY218;
					}
					#elif defined( _BACKGROUNDMODE_CUBEMAP )
					{
						staticSwitch1207 = BACKGROUND1202;
					}
					#elif defined( _BACKGROUNDMODE_COMBINED )
					{
						staticSwitch1207 = ( SKY218 * BACKGROUND1202 );
					}
					#else
					{
						staticSwitch1207 = SKY218;
					}
					#endif
					float staticSwitch1166 = ( float )0;
					#ifdef _ENABLESTARS_ON
					{
						staticSwitch1166 = saturate(  (0.1 + ( abs( IN.ase_texcoord1.xyz.y ) - 0.0 ) * ( 1.0 - 0.1 ) / ( _StarsHeightMask - 0.0 ) ) );
					}
					#else
					{
						staticSwitch1166 = 0.0;
					}
					#endif
					float3 break1223 = VertexPos1217;
					float lerpResult268 = lerp( 1.0 , ( unity_OrthoParams.y / unity_OrthoParams.x ) , unity_OrthoParams.w);
					half CAMERA_MODE300 = lerpResult268;
					float temp_output_673_0 = ( break1223.y * CAMERA_MODE300 );
					float3 staticSwitch1165 = ( float3 )0;
					#ifdef _ENABLESTARS_ON
					{
						float3 appendResult675 = (float3(break1223.x , temp_output_673_0 , break1223.z));
						staticSwitch1165 = appendResult675;
					}
					#else
					{
						staticSwitch1165 = float3( 0,0,0 );
					}
					#endif
					float3 staticSwitch1170 = ( float3 )0;
					#ifdef _ENABLESTARS_ON
					{
						half3 STARS1263 = ( _StarsColor.rgb * _StarsIntensity );
						float vertexToFrag856 = IN.ase_texcoord.w;
						float3 vertexToFrag763 = IN.ase_texcoord2.xyz;
						half Starts_Bottom_Mask1230 = step( 0.0 , temp_output_673_0 );
						float lerpResult1234 = lerp( 1.0 , saturate( Starts_Bottom_Mask1230 ) , _StarsBottomMask);
						half STARS_MASK630 = ( floor( ( vertexToFrag856 * ( texCUBE( _StarsCubemap, vertexToFrag763 ).g + _StarsSize ) ) ) * saturate( _StarsIntensity ) * lerpResult1234 );
						float3 lerpResult1264 = lerp( staticSwitch1207 , STARS1263 , STARS_MASK630);
						staticSwitch1170 = lerpResult1264;
					}
					#else
					{
						staticSwitch1170 = staticSwitch1207;
					}
					#endif
					float3 normalizeResult1258 = normalize( _SunDirection );
					float3 lerpResult1256 = lerp( PolyverseSunDir , normalizeResult1258 , _SunDirectionMode);
					half3 GlobalSunDirection1005 = -lerpResult1256;
					float staticSwitch1169 = ( float )0;
					#ifdef _ENABLESUN_ON
					{
						float dotResult988 = dot( GlobalSunDirection1005 , IN.ase_texcoord1.xyz );
						staticSwitch1169 = saturate( dotResult988 );
					}
					#else
					{
						staticSwitch1169 = 0.0;
					}
					#endif
					float2 staticSwitch1168 = ( float2 )0;
					#ifdef _ENABLESUN_ON
					{
						float3 temp_output_962_0 = cross( -lerpResult1256 , half3( 0, 1, 0 ) );
						float3 normalizeResult967 = normalize( temp_output_962_0 );
						float dotResult968 = dot( normalizeResult967 , IN.ase_texcoord1.xyz );
						float3 normalizeResult965 = normalize( cross( GlobalSunDirection1005 , temp_output_962_0 ) );
						float dotResult969 = dot( normalizeResult965 , IN.ase_texcoord1.xyz );
						float2 appendResult970 = (float2(dotResult968 , dotResult969));
						float2 break972 = appendResult970;
						float2 appendResult980 = (float2(break972.x , ( break972.y * CAMERA_MODE300 )));
						float lerpResult1246 = lerp( 20.0 , 2.0 , _SunSize);
						staticSwitch1168 = (( appendResult980 * lerpResult1246 )*0.5 + 0.5);
					}
					#else
					{
						staticSwitch1168 = float2( 0,0 );
					}
					#endif
					float3 staticSwitch1167 = ( float3 )0;
					#ifdef _ENABLESUN_ON
					{
						float2 vertexToFrag993 = IN.ase_texcoord3.xy;
						float4 tex2DNode995 = tex2D( _SunTexture, vertexToFrag993 );
						half3 SUN1004 = ( tex2DNode995.r * _SunColor.rgb * max( _SunIntensity, 1.0 ) );
						float vertexToFrag997 = IN.ase_texcoord2.w;
						half SUN_MASK1003 = ( tex2DNode995.a * saturate( _SunIntensity ) * vertexToFrag997 );
						float3 lerpResult176 = lerp( staticSwitch1170 , SUN1004 , SUN_MASK1003);
						staticSwitch1167 = lerpResult176;
					}
					#else
					{
						staticSwitch1167 = staticSwitch1170;
					}
					#endif
					float3 staticSwitch1163 = ( float3 )0;
					#ifdef _ENABLECLOUDS_ON
					{
						float3 break1225 = VertexPos1217;
						float3 appendResult246 = (float3(break1225.x , ( ( break1225.y + ( _CloudsHeight * -1.0 ) ) * CAMERA_MODE300 ) , break1225.z));
						staticSwitch1163 = appendResult246;
					}
					#else
					{
						staticSwitch1163 = float3( 0,0,0 );
					}
					#endif
					float3 staticSwitch1162 = ( float3 )0;
					#ifdef _ENABLECLOUDS_ON
					{
						float3 vertexToFrag1133 = IN.ase_texcoord4.xyz;
						float4 texCUBENode41 = texCUBE( _CloudsCubemap, vertexToFrag1133 );
						float3 lerpResult101 = lerp( _CloudsShadowColor.rgb , _CloudsLightColor.rgb , texCUBENode41.rgb);
						half3 CLOUDS222 = lerpResult101;
						half CLOUDS_MASK223 = ( texCUBENode41.a * _CloudsIntensity );
						float3 lerpResult227 = lerp( staticSwitch1167 , CLOUDS222 , CLOUDS_MASK223);
						staticSwitch1162 = lerpResult227;
					}
					#else
					{
						staticSwitch1162 = staticSwitch1167;
					}
					#endif
					float4 staticSwitch921 = ( float4 )0;
					#ifdef _ENABLEBUILTINFOG_ON
					{
						float temp_output_7_0_g8 = 0.0;
						float lerpResult678 = lerp( saturate( pow( ( ( abs( VertexPos1217.y ) - temp_output_7_0_g8 ) / ( _FogHeight - temp_output_7_0_g8 ) ) , ( 1.0 - _FogSmoothness ) ) ) , 0.0 , _FogFill);
						half FOG_MASK359 = lerpResult678;
						float4 lerpResult317 = lerp( unity_FogColor , float4( staticSwitch1162 , 0.0 ) , FOG_MASK359);
						staticSwitch921 = lerpResult317;
					}
					#else
					{
						staticSwitch921 = float4( staticSwitch1162 , 0.0 );
					}
					#endif
					

					float3 Color = staticSwitch921.rgb;
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
{"type":"AmplifyShaderEditor.Vector3Node, AmplifyShaderEditor","id":1259,"pos":[-1408,-864],"params":["Half","False","Property","_SunDirection","Sun Direction","25","0","Create","True","0","0","0","False","1","StyledVector(18)","False","Object","-1","","0,0,1","0,0,0","0","4","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1236,"pos":[-896,-4592],"params":["Inherit","False","Property","_SkyboxOffset","Skybox Offset","40","0","Create","True","0","0","0","False","0","False","Object","-1","","0","0.21","-1","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.NormalizeNode, AmplifyShaderEditor","id":1258,"pos":[-1232,-864],"params":["Inherit","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.Vector3Node, AmplifyShaderEditor","id":1257,"pos":[-1408,-1024],"params":["Half","False","Global","PolyverseSunDir","PolyverseSunDir","38","0","Create","True","0","0","0","False","0","False","Object","-1","","0,0,0","0,0,0","0","4","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1260,"pos":[-1408,-704],"params":["Half","False","Property","_SunDirectionMode","Sun Direction","24","1","[Enum]","Create","False","0","2","Global From Script","0","Local From Material","1","0","False","0","False","Object","-1","","0","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.OrthoParams, AmplifyShaderEditor","id":267,"pos":[-896,-5248],"params":["Inherit","False","0","5","FLOAT4","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.NegateNode, AmplifyShaderEditor","id":1237,"pos":[-608,-4592],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":1256,"pos":[-1088,-1024],"params":["Inherit","False","3","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1007,"pos":[-448,-5248],"params":["Half","False","Constant","_Float7","Float 7","47","0","Create","True","0","0","0","False","0","False","Object","-1","","1","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleDivideOpNode, AmplifyShaderEditor","id":309,"pos":[-592,-5248],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.PosVertexDataNode, AmplifyShaderEditor","id":1238,"pos":[-896,-4736],"params":["Inherit","False","0","0","5","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.DynamicAppendNode, AmplifyShaderEditor","id":1239,"pos":[-448,-4672],"params":["Inherit","False","FLOAT3","4","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1211,"pos":[-896,-4320],"params":["Inherit","False","Property","_SkyboxRotation","Skybox Rotation","41","0","Create","True","0","0","0","False","0","False","Object","-1","","0","0.115","0","360","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.Vector3Node, AmplifyShaderEditor","id":961,"pos":[-896,-864],"params":["Half","False","Constant","_Vector2","Vector 2","9","0","Create","True","0","0","0","False","0","False","Object","-1","","0,1,0","0,0,0","0","4","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3"]}
{"type":"AmplifyShaderEditor.NegateNode, AmplifyShaderEditor","id":1255,"pos":[-896,-1024],"params":["Inherit","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":1005,"pos":[-640,-1024],"params":["Half","False","GlobalSunDirection","-1","True","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":268,"pos":[-256,-5248],"params":["Inherit","False","3","0","FLOAT","1","False","1","FLOAT","0.5","False","2","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.VertexToFragmentNode, AmplifyShaderEditor","id":1216,"pos":[384,-4736],"params":["Inherit","False","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RadiansOpNode, AmplifyShaderEditor","id":1251,"pos":[-608,-4320],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.Vector3Node, AmplifyShaderEditor","id":1212,"pos":[-896,-4480],"params":["Inherit","False","Property","_SkyboxRotationAxis","Skybox Rotation Axis","42","0","Create","True","0","0","0","False","1","StyledVector(18)","False","Object","-1","","0,1,0","0,1,0","0","4","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3"]}
{"type":"AmplifyShaderEditor.SimpleAddOpNode, AmplifyShaderEditor","id":1240,"pos":[-256,-4736],"params":["Inherit","False","2","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.CrossProductOpNode, AmplifyShaderEditor","id":962,"pos":[-576,-896],"params":["Inherit","False","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":300,"pos":[64,-5248],"params":["Half","False","CAMERA_MODE","-1","True","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.CrossProductOpNode, AmplifyShaderEditor","id":964,"pos":[-384,-1024],"params":["Inherit","False","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":1217,"pos":[640,-4736],"params":["Half","False","VertexPos","-1","True","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.FunctionNode, AmplifyShaderEditor","id":1252,"pos":[-64,-4736],"params":["Inherit","False","Compute Rotation","-1","","3","d0fbe60918b2bc146b660122744f3d02","0","4","38","FLOAT3","0,0,0","False","72","FLOAT3","0,0,0","False","41","FLOAT3","0,0,0","False","43","FLOAT","0","False","1","FLOAT3","19"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1222,"pos":[-896,-1792],"params":["Inherit","False","1217","VertexPos","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.NormalizeNode, AmplifyShaderEditor","id":967,"pos":[-384,-896],"params":["Inherit","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.NormalizeNode, AmplifyShaderEditor","id":965,"pos":[-192,-1024],"params":["Inherit","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.PosVertexDataNode, AmplifyShaderEditor","id":1176,"pos":[-384,-640],"params":["Inherit","False","0","0","5","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":674,"pos":[-896,-1600],"params":["Inherit","False","300","CAMERA_MODE","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.NormalizeNode, AmplifyShaderEditor","id":1241,"pos":[192,-4736],"params":["Inherit","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.BreakToComponentsNode, AmplifyShaderEditor","id":1223,"pos":[-704,-1792],"params":["Inherit","False","FLOAT3","1","0","FLOAT3","0,0,0","False","16","FLOAT","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT","5","FLOAT","6","FLOAT","7","FLOAT","8","FLOAT","9","FLOAT","10","FLOAT","11","FLOAT","12","FLOAT","13","FLOAT","14","FLOAT","15"]}
{"type":"AmplifyShaderEditor.DotProductOpNode, AmplifyShaderEditor","id":968,"pos":[0,-896],"params":["Inherit","False","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.PosVertexDataNode, AmplifyShaderEditor","id":1175,"pos":[1024,-1792],"params":["Inherit","False","0","0","5","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.DotProductOpNode, AmplifyShaderEditor","id":969,"pos":[0,-1024],"params":["Inherit","False","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":673,"pos":[-640,-1616],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1171,"pos":[1648,-1696],"params":["Half","False","Constant","_Float0","Float 0","47","0","Create","True","0","0","0","False","0","False","Object","-1","","0.1","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1017,"pos":[1408,-1680],"params":["Half","False","Constant","_Float17","Float 17","47","0","Create","True","0","0","0","False","0","False","Object","-1","","0","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.DynamicAppendNode, AmplifyShaderEditor","id":970,"pos":[160,-1024],"params":["Inherit","False","FLOAT2","4","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","1","FLOAT2","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":831,"pos":[1600,-1600],"params":["Half","False","Property","_StarsHeightMask","Stars Height Mask","16","0","Create","True","0","0","0","False","0","False","Object","-1","","0","0","0","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.DynamicAppendNode, AmplifyShaderEditor","id":675,"pos":[-448,-1792],"params":["Inherit","False","FLOAT3","4","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.AbsOpNode, AmplifyShaderEditor","id":828,"pos":[1664,-1792],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1018,"pos":[1408,-1600],"params":["Half","False","Constant","_Float18","Float 18","47","0","Create","True","0","0","0","False","0","False","Object","-1","","1","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":971,"pos":[320,-896],"params":["Inherit","False","300","CAMERA_MODE","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.TFHCRemapNode, AmplifyShaderEditor","id":832,"pos":[1920,-1792],"params":["Inherit","False","5","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","1","False","3","FLOAT","0","False","4","FLOAT","1","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.BreakToComponentsNode, AmplifyShaderEditor","id":972,"pos":[320,-1024],"params":["Inherit","False","FLOAT2","1","0","FLOAT2","0,0","False","16","FLOAT","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT","5","FLOAT","6","FLOAT","7","FLOAT","8","FLOAT","9","FLOAT","10","FLOAT","11","FLOAT","12","FLOAT","13","FLOAT","14","FLOAT","15"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":1165,"pos":[-208,-1744],"params":["Float","False","Property","_ENABLESTARS_ON","Enable Stars","11","0","Create","False","0","0","0","False","0","False","","0","0","0","False","","Toggle","2","Key0","Key1","Reference","1170","False","True","All","False","False","True","9","1","FLOAT3","0,0,0","False","0","FLOAT3","0,0,0","False","2","FLOAT3","0,0,0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT3","0,0,0","False","6","FLOAT3","0,0,0","False","7","FLOAT3","0,0,0","False","8","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":976,"pos":[640,-928],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":975,"pos":[0,-768],"params":["Half","False","Property","_SunSize","Sun Size","22","0","Create","True","0","0","0","False","0","False","Object","-1","","0.5","0.5","0.1","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1090,"pos":[-896,320],"params":["Half","False","Constant","_Float31","Float 31","53","0","Create","True","0","0","0","False","0","False","Object","-1","","-1","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":241,"pos":[-896,192],"params":["Half","False","Property","_CloudsHeight","Clouds Height","30","0","Create","True","0","0","0","False","0","False","Object","-1","","0","0","-0.5","0.5","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1220,"pos":[-896,-2944],"params":["Inherit","False","1217","VertexPos","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":212,"pos":[-384,-2496],"params":["Half","False","Property","_EquatorHeight","Equator Height","6","0","Create","True","0","0","0","False","0","False","Object","-1","","0.5","0.5","0","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.AbsOpNode, AmplifyShaderEditor","id":287,"pos":[-192,-2944],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.VertexToFragmentNode, AmplifyShaderEditor","id":763,"pos":[48,-1728],"params":["Inherit","False","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.SaturateNode, AmplifyShaderEditor","id":822,"pos":[2112,-1792],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StepOpNode, AmplifyShaderEditor","id":1243,"pos":[-448,-1616],"params":["Inherit","False","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.DynamicAppendNode, AmplifyShaderEditor","id":980,"pos":[832,-1024],"params":["Inherit","False","FLOAT2","4","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","1","FLOAT2","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":1246,"pos":[640,-768],"params":["Inherit","False","3","0","FLOAT","20","False","1","FLOAT","2","False","2","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":278,"pos":[-512,256],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.BreakToComponentsNode, AmplifyShaderEditor","id":1221,"pos":[-704,-2944],"params":["Inherit","False","FLOAT3","1","0","FLOAT3","0,0,0","False","16","FLOAT","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT","5","FLOAT","6","FLOAT","7","FLOAT","8","FLOAT","9","FLOAT","10","FLOAT","11","FLOAT","12","FLOAT","13","FLOAT","14","FLOAT","15"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":471,"pos":[-384,-2368],"params":["Half","False","Property","_EquatorSmoothness","Equator Smoothness","7","0","Create","True","0","0","0","False","0","False","Object","-1","","0.5","0.5","0.01","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.FunctionNode, AmplifyShaderEditor","id":1254,"pos":[0,-2560],"params":["Inherit","False","Remap To 0-1","-1","","7","d6eb10c035ec242479a0519a8ea4fd0f","0","3","6","FLOAT","0","False","7","FLOAT","0","False","8","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":1230,"pos":[-208,-1600],"params":["Half","False","Starts_Bottom_Mask","-1","True","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1224,"pos":[-896,0],"params":["Inherit","False","1217","VertexPos","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":619,"pos":[384,-1600],"params":["Half","False","Property","_StarsSize","Stars Size","13","0","Create","True","0","0","0","False","1","Space(10)","False","Object","-1","","0.5","0.5","0","0.99","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SamplerNode, AmplifyShaderEditor","id":564,"pos":[384,-1792],"params":["Inherit","True","Property","_StarsCubemap","Stars Cubemap","12","1","[NoScaleOffset]","Create","True","0","0","0","False","2","Space(10)","StyledTextureSingleLine","False","","-1","None","4136c4d5d7422cf49a21b885d09f4518","True","0","False","white","LockedToCube","False","Object","-1","Auto","Cube","False","8","0","SAMPLERCUBE","","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT","1","False","6","FLOAT","0","False","7","SAMPLERSTATE","","False","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":1166,"pos":[2305.3,-1744],"params":["Float","False","Property","_ENABLESTARS_ON","Enable Stars","11","0","Create","False","0","0","0","False","0","False","","0","0","0","False","","Toggle","2","Key0","Key1","Reference","1170","False","True","All","False","False","True","9","1","FLOAT","0","False","0","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","4","FLOAT","0","False","5","FLOAT","0","False","6","FLOAT","0","False","7","FLOAT","0","False","8","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":985,"pos":[1024,-1024],"params":["Inherit","False","2","2","0","FLOAT2","0,0","False","1","FLOAT","5","False","1","FLOAT2","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1028,"pos":[2816,-640],"params":["Inherit","False","1005","GlobalSunDirection","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.PosVertexDataNode, AmplifyShaderEditor","id":1177,"pos":[2816,-512],"params":["Inherit","False","0","0","5","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1152,"pos":[-256,320],"params":["Inherit","False","300","CAMERA_MODE","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleAddOpNode, AmplifyShaderEditor","id":244,"pos":[-256,192],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SaturateNode, AmplifyShaderEditor","id":208,"pos":[224,-2560],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.OneMinusNode, AmplifyShaderEditor","id":475,"pos":[-64,-2368],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StepOpNode, AmplifyShaderEditor","id":1244,"pos":[-512,-2432],"params":["Inherit","False","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleAddOpNode, AmplifyShaderEditor","id":626,"pos":[768,-1552],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.BreakToComponentsNode, AmplifyShaderEditor","id":1225,"pos":[-704,0],"params":["Inherit","False","FLOAT3","1","0","FLOAT3","0,0,0","False","16","FLOAT","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT","5","FLOAT","6","FLOAT","7","FLOAT","8","FLOAT","9","FLOAT","10","FLOAT","11","FLOAT","12","FLOAT","13","FLOAT","14","FLOAT","15"]}
{"type":"AmplifyShaderEditor.VertexToFragmentNode, AmplifyShaderEditor","id":856,"pos":[2560,-1728],"params":["Inherit","False","False","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.DotProductOpNode, AmplifyShaderEditor","id":988,"pos":[3072,-640],"params":["Inherit","False","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.ScaleAndOffsetNode, AmplifyShaderEditor","id":1247,"pos":[1280,-1024],"params":["Inherit","False","3","0","FLOAT2","0,0","False","1","FLOAT","0.5","False","2","FLOAT","0.5","False","1","FLOAT2","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1151,"pos":[0,192],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1226,"pos":[-896,896],"params":["Inherit","False","1217","VertexPos","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1231,"pos":[3200,-1280],"params":["Inherit","False","1230","Starts_Bottom_Mask","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.ColorNode, AmplifyShaderEditor","id":182,"pos":[640,-2592],"params":["Half","False","Property","_SkyColor","Sky Color","3","0","Create","True","0","0","0","False","1","Space(10)","False","Object","-1","","0.4980392,0.7450981,1,1","0.4980389,0.7450981,1,1","False","True","0","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1219,"pos":[-896,-3584],"params":["Inherit","False","1217","VertexPos","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.ColorNode, AmplifyShaderEditor","id":194,"pos":[640,-2768],"params":["Half","False","Property","_GroundColor","Ground Color","5","0","Create","True","0","0","0","False","0","False","Object","-1","","0.4980392,0.497,0,1","0.4980389,0.497,0,1","False","True","0","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.PowerNode, AmplifyShaderEditor","id":470,"pos":[400,-2384],"params":["Inherit","False","True","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RelayNode, AmplifyShaderEditor","id":303,"pos":[896,-2448],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":876,"pos":[2944,-1472],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SaturateNode, AmplifyShaderEditor","id":994,"pos":[3232,-640],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":1168,"pos":[1568,-976],"params":["Float","False","Property","_ENABLESUN_ON","Enable Sun","19","0","Create","False","0","0","0","False","0","False","","0","0","0","False","","Toggle","2","Key0","Key1","Reference","1167","False","True","All","False","False","True","9","1","FLOAT2","0,0","False","0","FLOAT2","0,0","False","2","FLOAT2","0,0","False","3","FLOAT2","0,0","False","4","FLOAT2","0,0","False","5","FLOAT2","0,0","False","6","FLOAT2","0,0","False","7","FLOAT2","0,0","False","8","FLOAT2","0,0","False","1","FLOAT2","0"]}
{"type":"AmplifyShaderEditor.DynamicAppendNode, AmplifyShaderEditor","id":246,"pos":[128,0],"params":["Inherit","False","FLOAT3","4","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.BreakToComponentsNode, AmplifyShaderEditor","id":1227,"pos":[-704,896],"params":["Inherit","False","FLOAT3","1","0","FLOAT3","0,0,0","False","16","FLOAT","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT","5","FLOAT","6","FLOAT","7","FLOAT","8","FLOAT","9","FLOAT","10","FLOAT","11","FLOAT","12","FLOAT","13","FLOAT","14","FLOAT","15"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1232,"pos":[3200,-1216],"params":["Half","False","Property","_StarsBottomMask","Stars Bottom Mask","17","0","Create","True","0","0","0","False","1","StyledToggle","False","Object","-1","","0","1","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SaturateNode, AmplifyShaderEditor","id":1233,"pos":[3440,-1280],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":629,"pos":[3200,-1600],"params":["Half","False","Property","_StarsIntensity","Stars Intensity","15","0","Create","True","0","0","0","False","0","False","Object","-1","","2","2.71","0","5","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":180,"pos":[1024,-2752],"params":["Inherit","False","3","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.ColorNode, AmplifyShaderEditor","id":181,"pos":[640,-2944],"params":["Half","False","Property","_EquatorColor","Equator Color","4","0","Create","True","0","0","0","False","0","False","Object","-1","","1,0.747,0,1","1,0.747,0,1","False","True","0","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.RelayNode, AmplifyShaderEditor","id":417,"pos":[1152,-2368],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1200,"pos":[-640,-3392],"params":["Half","False","Property","_BackgroundExposure","Background Exposure","9","0","Create","True","0","0","0","False","1","Space(10)","False","Object","-1","","1","1","0","8","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SamplerNode, AmplifyShaderEditor","id":1199,"pos":[-640,-3584],"params":["Inherit","True","Property","_BackgroundCubemap","Background Cubemap","8","1","[NoScaleOffset]","Create","True","0","0","0","False","2","Space(10)","StyledTextureSingleLine","False","","-1","None","None","True","0","False","black","LockedToCube","False","Object","-1","Auto","Cube","False","8","0","SAMPLERCUBE","","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT","1","False","6","FLOAT","0","False","7","SAMPLERSTATE","","False","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.FloorOpNode, AmplifyShaderEditor","id":886,"pos":[3200,-1472],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.VertexToFragmentNode, AmplifyShaderEditor","id":993,"pos":[1808,-960],"params":["Inherit","False","False","False","1","0","FLOAT2","0,0","False","1","FLOAT2","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":996,"pos":[2176,-576],"params":["Half","False","Property","_SunIntensity","Sun Intensity","23","0","Create","True","0","0","0","False","0","False","Object","-1","","1","1","0","10","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":1169,"pos":[3392,-640],"params":["Float","False","Property","_ENABLESUN_ON","Enable Sun","19","0","Create","False","0","0","0","False","0","False","","0","0","0","False","","Toggle","2","Key0","Key1","Reference","1167","False","True","All","False","False","True","9","1","FLOAT","0","False","0","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","4","FLOAT","0","False","5","FLOAT","0","False","6","FLOAT","0","False","7","FLOAT","0","False","8","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":1163,"pos":[384,80],"params":["Float","False","Property","_ENABLECLOUDS_ON","Enable Clouds","27","0","Create","False","0","0","0","False","0","False","","0","0","0","False","","Toggle","2","Key0","Key1","Reference","1162","False","True","All","False","False","True","9","1","FLOAT3","0,0,0","False","0","FLOAT3","0,0,0","False","2","FLOAT3","0,0,0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT3","0,0,0","False","6","FLOAT3","0,0,0","False","7","FLOAT3","0,0,0","False","8","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.AbsOpNode, AmplifyShaderEditor","id":314,"pos":[-512,896],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":325,"pos":[-896,1216],"params":["Half","False","Property","_FogSmoothness","Fog Smoothness","37","0","Create","True","0","0","0","False","0","False","Object","-1","","0","0","0.01","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":313,"pos":[-896,1088],"params":["Half","False","Property","_FogHeight","Fog Height","36","0","Create","True","0","0","0","False","1","Space(10)","False","Object","-1","","0","0","0","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":1234,"pos":[3584,-1280],"params":["Inherit","False","3","0","FLOAT","1","False","1","FLOAT","1","False","2","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.ColorNode, AmplifyShaderEditor","id":1261,"pos":[3200,-1792],"params":["Half","False","Property","_StarsColor","Stars Color","14","0","Create","True","0","0","0","False","0","False","Object","-1","","1,1,1,1","0,0,0,0","True","True","0","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.SaturateNode, AmplifyShaderEditor","id":1267,"pos":[3584,-1392],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1201,"pos":[-256,-3584],"params":["Inherit","False","2","2","0","FLOAT3","0,0,0","False","1","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":288,"pos":[1280,-2944],"params":["Inherit","False","3","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":882,"pos":[3840,-1472],"params":["Inherit","False","3","3","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SaturateNode, AmplifyShaderEditor","id":1249,"pos":[2688,-832],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMaxOpNode, AmplifyShaderEditor","id":1250,"pos":[2560,-576],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","1","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.ColorNode, AmplifyShaderEditor","id":998,"pos":[2176,-768],"params":["Half","False","Property","_SunColor","Sun Color","21","0","Create","True","0","0","0","False","1","Space(10)","False","Object","-1","","1,1,1,1","1,1,1,1","False","True","0","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.SamplerNode, AmplifyShaderEditor","id":995,"pos":[2176,-1024],"params":["Inherit","True","Property","_SunTexture","Sun Texture","20","1","[NoScaleOffset]","Create","True","0","0","0","False","2","Space(10)","StyledTextureSingleLine","False","","-1","None","None","True","0","False","black","Auto","False","Object","-1","Auto","Texture2D","False","8","0","SAMPLER2D","","False","1","FLOAT2","0,0","False","2","FLOAT","0","False","3","FLOAT2","0,0","False","4","FLOAT2","0,0","False","5","FLOAT","1","False","6","FLOAT","0","False","7","SAMPLERSTATE","","False","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.VertexToFragmentNode, AmplifyShaderEditor","id":997,"pos":[3632,-640],"params":["Inherit","False","False","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.VertexToFragmentNode, AmplifyShaderEditor","id":1133,"pos":[640,96],"params":["Inherit","False","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.OneMinusNode, AmplifyShaderEditor","id":329,"pos":[-256,1216],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.FunctionNode, AmplifyShaderEditor","id":1253,"pos":[-320,896],"params":["Inherit","False","Remap To 0-1","-1","","8","d6eb10c035ec242479a0519a8ea4fd0f","0","3","6","FLOAT","0","False","7","FLOAT","0","False","8","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1266,"pos":[3840,-1792],"params":["Inherit","False","2","2","0","FLOAT3","0,0,0","False","1","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":1202,"pos":[256,-3584],"params":["Half","False","BACKGROUND","-1","True","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":218,"pos":[1664,-2944],"params":["Half","False","SKY","-1","True","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":630,"pos":[4032,-1472],"params":["Half","False","STARS_MASK","-1","True","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":1263,"pos":[4032,-1792],"params":["Half","False","STARS","-1","True","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1002,"pos":[2688,-1024],"params":["Inherit","False","3","3","0","FLOAT","0","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1001,"pos":[4096,-928],"params":["Inherit","False","3","3","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SamplerNode, AmplifyShaderEditor","id":41,"pos":[1024,0],"params":["Inherit","True","Property","_CloudsCubemap","Clouds Cubemap","28","1","[NoScaleOffset]","Create","True","0","0","0","False","2","Space(10)","StyledTextureSingleLine","False","","-1","None","5bf545dec997743419de1df4195b3a0d","True","0","False","black","LockedToCube","False","Object","-1","Auto","Cube","False","8","0","SAMPLERCUBE","","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT","1","False","6","FLOAT","0","False","7","SAMPLERSTATE","","False","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1229,"pos":[1024,256],"params":["Half","False","Property","_CloudsIntensity","Clouds Intensity","29","0","Create","True","0","0","0","False","1","Space(10)","False","Object","-1","","1","1","0","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.ColorNode, AmplifyShaderEditor","id":232,"pos":[1792,0],"params":["Half","False","Property","_CloudsShadowColor","Clouds Shadow Color","32","0","Create","True","0","0","0","False","0","False","Object","-1","","0.4980392,0.7450981,1,1","1,0.1853116,0,1","False","True","0","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.ColorNode, AmplifyShaderEditor","id":261,"pos":[1792,224],"params":["Half","False","Property","_CloudsLightColor","Clouds Light Color","31","0","Create","True","0","0","0","False","0","False","Object","-1","","1,1,1,1","1,1,1,1","False","True","0","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.PowerNode, AmplifyShaderEditor","id":677,"pos":[-64,896],"params":["Inherit","False","False","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1204,"pos":[-896,-6576],"params":["Inherit","False","1202","BACKGROUND","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1205,"pos":[-896,-6656],"params":["Inherit","False","218","SKY","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1206,"pos":[-640,-6528],"params":["Inherit","False","2","2","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":632,"pos":[-384,-6336],"params":["Inherit","False","630","STARS_MASK","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1265,"pos":[-384,-6432],"params":["Inherit","False","1263","STARS","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":1004,"pos":[4352,-1024],"params":["Half","False","SUN","-1","True","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":1003,"pos":[4352,-768],"params":["Half","False","SUN_MASK","-1","True","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":1228,"pos":[1377.863,200.6354],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":101,"pos":[2176,0],"params":["Inherit","False","3","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","2","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1110,"pos":[128,1088],"params":["Half","False","Constant","_Float41","Float 41","55","0","Create","True","0","0","0","False","0","False","Object","-1","","0","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":679,"pos":[128,1216],"params":["Half","False","Property","_FogFill","Fog Fill","38","0","Create","True","0","0","0","False","0","False","Object","-1","","0","0","0","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SaturateNode, AmplifyShaderEditor","id":316,"pos":[128,896],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":1207,"pos":[-384,-6656],"params":["Float","False","Property","_BackgroundMode","Background Mode","2","0","Create","True","0","0","0","False","0","False","","0","0","0","True","","KeywordEnum","3","Colors","Cubemap","Combined","Create","True","True","All","False","False","True","9","1","FLOAT3","0,0,0","False","0","FLOAT3","0,0,0","False","2","FLOAT3","0,0,0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT3","0,0,0","False","6","FLOAT3","0,0,0","False","7","FLOAT3","0,0,0","False","8","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":1264,"pos":[0,-6528],"params":["Inherit","False","3","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1112,"pos":[256,-6432],"params":["Inherit","False","1004","SUN","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":1111,"pos":[256,-6336],"params":["Inherit","False","1003","SUN_MASK","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":222,"pos":[2560,0],"params":["Half","False","CLOUDS","-1","True","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":223,"pos":[1536,128],"params":["Half","False","CLOUDS_MASK","-1","True","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":678,"pos":[384,896],"params":["Inherit","False","3","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":1170,"pos":[256,-6656],"params":["Float","False","Property","_EnableStars","Enable Stars","11","0","Create","True","0","0","0","False","0","False","","0","0","0","True","","Toggle","2","Key0","Key1","Create","True","True","All","False","False","True","9","1","FLOAT3","0,0,0","False","0","FLOAT3","0,0,0","False","2","FLOAT3","0,0,0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT3","0,0,0","False","6","FLOAT3","0,0,0","False","7","FLOAT3","0,0,0","False","8","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":176,"pos":[576,-6528],"params":["Inherit","False","3","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":228,"pos":[768,-6432],"params":["Inherit","False","222","CLOUDS","1","0","OBJECT","","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":229,"pos":[768,-6336],"params":["Inherit","False","223","CLOUDS_MASK","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":359,"pos":[640,896],"params":["Half","False","FOG_MASK","-1","True","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":1167,"pos":[768,-6656],"params":["Float","False","Property","_EnableSun","Enable Sun","19","0","Create","True","0","0","0","False","0","False","","0","0","0","True","","Toggle","2","Key0","Key1","Create","True","True","All","False","False","True","9","1","FLOAT3","0,0,0","False","0","FLOAT3","0,0,0","False","2","FLOAT3","0,0,0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT3","0,0,0","False","6","FLOAT3","0,0,0","False","7","FLOAT3","0,0,0","False","8","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":227,"pos":[1088,-6528],"params":["Inherit","False","3","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":436,"pos":[1280,-6336],"params":["Inherit","False","359","FOG_MASK","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.FogAndAmbientColorsNode, AmplifyShaderEditor","id":312,"pos":[1280,-6432],"params":["Inherit","False","unity_FogColor","0","1","COLOR","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":1162,"pos":[1280,-6656],"params":["Float","False","Property","_EnableClouds","Enable Clouds","27","0","Create","True","0","0","0","False","0","False","","0","0","0","True","","Toggle","2","Key0","Key1","Create","True","True","All","False","False","True","9","1","FLOAT3","0,0,0","False","0","FLOAT3","0,0,0","False","2","FLOAT3","0,0,0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT3","0,0,0","False","6","FLOAT3","0,0,0","False","7","FLOAT3","0,0,0","False","8","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":317,"pos":[1664,-6528],"params":["Inherit","False","3","0","COLOR","0,0,0,0","False","1","COLOR","0,0,0,0","False","2","FLOAT","0","False","1","COLOR","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1218,"pos":[256,-7296],"params":["Half","False","Property","_SkyboxCat","[ Skybox Cat ]","39","0","Create","True","0","0","0","True","1","StyledCategory(Skybox Settings)","False","Object","-1","","1","1","1","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1192,"pos":[96,-7296],"params":["Half","False","Property","_FogCat","[ Fog Cat ]","33","0","Create","True","0","0","0","True","1","StyledCategory(Fog Settings)","False","Object","-1","","1","1","1","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1180,"pos":[-896,-7296],"params":["Half","False","Property","_Banner","< Banner >","0","0","Create","True","0","0","0","True","1","StyledBanner(Polyverse Skies Simple)","False","Object","-1","","1","1","1","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1209,"pos":[-896,1344],"params":["Inherit","False","Property","_EnableFogMessage","EnableFogMessage","35","0","Create","True","0","0","0","True","1","StyledMessage(Info, The fog color is controlled by the fog color set in the Lighting panel., _EnableBuiltinFog, 1, 10, 0)","False","Object","-1","","0","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1190,"pos":[-240,-7296],"params":["Half","False","Property","_SunCat","[ Sun Cat ]","18","0","Create","True","0","0","0","True","1","StyledCategory(Sun Settings)","False","Object","-1","","1","1","1","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":921,"pos":[1920,-6656],"params":["Float","False","Property","_EnableBuiltinFog","Enable Fog","34","0","Create","False","0","0","0","False","0","False","","0","0","0","True","","Toggle","2","Key0","Key1","Create","True","True","All","False","False","True","9","1","COLOR","0,0,0,0","False","0","COLOR","0,0,0,0","False","2","COLOR","0,0,0,0","False","3","COLOR","0,0,0,0","False","4","COLOR","0,0,0,0","False","5","COLOR","0,0,0,0","False","6","COLOR","0,0,0,0","False","7","COLOR","0,0,0,0","False","8","COLOR","0,0,0,0","False","1","COLOR","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1203,"pos":[-640,-7296],"params":["Half","False","Property","_BackgroundCat","[ Background Cat ]","1","0","Create","True","0","0","0","True","1","StyledCategory(Background Settings, 5, 10)","False","Object","-1","","1","1","1","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1191,"pos":[-80,-7296],"params":["Half","False","Property","_CloudsCat","[ Clouds Cat ]","26","0","Create","True","0","0","0","True","1","StyledCategory(Clouds Settings)","False","Object","-1","","1","1","1","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":1189,"pos":[-416,-7296],"params":["Half","False","Property","_StarsCat","[ Stars Cat ]","10","0","Create","True","0","0","0","True","1","StyledCategory(Stars Settings)","False","Object","-1","","1","1","1","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.TemplateMultiPassMasterNode, AmplifyShaderEditor","id":1197,"pos":[2432,-6656],"params":["Float","False","True","-1","2","PolyverseSkiesAsset.MaterialGUI","100","12","BOXOPHOBIC/Polyverse Skies/Simple","0770190933193b94aaa3065e307002fa","True","Unlit","0","1","Unlit","8","False","True","0","1","False","","0","False","","0","1","False","","0","False","","True","0","False","","0","False","","False","False","False","False","False","False","False","False","False","True","0","False","","True","True","0","False","","False","True","True","True","True","True","0","False","","False","False","False","False","False","False","False","True","False","255","False","","255","False","","255","False","","7","False","","1","False","","1","False","","1","False","","7","False","","1","False","","1","False","","1","False","","True","True","2","False","","False","True","False","0","False","","0","False","","True","2","False","","True","3","RenderType=Background=RenderType","Queue=Background=Queue=0","PreviewType=Skybox","True","2","True","14","all","0","True","True","1","1","False","","0","False","","1","1","False","","0","False","","True","0","False","","0","False","","False","False","False","False","False","False","False","False","False","False","False","True","0","False","","False","True","True","True","True","True","0","False","","False","False","False","False","False","False","False","True","False","0","False","","255","False","","255","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","True","True","2","False","","True","0","False","","True","False","0","False","","0","False","","False","False","False","False","0","","0","0","Standard","10","Surface","0","0","  Keep Alpha","0","0","  Blend","0","0","Alpha Clipping","0","0","  Use Shadow Threshold","0","0","Cast Shadows","0","639270861294225668","Write Depth","0","0","  Conservative","0","0","Extra Pre Pass","0","0","Vertex Position","1","0","0","3","False","True","False","False","","False","0"]}
{"type":"AmplifyShaderEditor.TemplateMultiPassMasterNode, AmplifyShaderEditor","id":1268,"pos":[2432,-6656],"params":["Float","False","False","-1","3","AmplifyShaderEditor.MaterialInspector","0","1","New Amplify Shader","0770190933193b94aaa3065e307002fa","True","ExtraPrePass","0","0","ExtraPrePass","0","False","True","1","1","False","","0","False","","1","1","False","","0","False","","True","1","False","","1","False","","False","False","False","False","False","False","False","False","False","True","0","False","","False","True","0","False","","False","True","True","True","True","True","0","False","","False","False","False","False","False","False","False","True","False","0","False","","255","False","","255","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","False","True","1","False","","False","True","True","0","False","","0","False","","True","1","False","","True","1","RenderType=Opaque=RenderType","True","3","True","14","all","0","False","True","1","1","False","","0","False","","0","1","False","","0","False","","False","False","False","False","False","False","False","False","False","False","False","False","True","0","False","","False","True","True","True","True","True","0","False","","False","False","False","False","False","False","False","True","False","0","False","","255","False","","255","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","False","True","1","False","","True","3","False","","True","True","0","False","","0","False","","False","False","False","False","0","","0","0","Standard","0","True","0"]}
{"type":"AmplifyShaderEditor.TemplateMultiPassMasterNode, AmplifyShaderEditor","id":1269,"pos":[2432,-6646],"params":["Float","False","False","-1","3","AmplifyShaderEditor.MaterialInspector","0","1","New Amplify Shader","0770190933193b94aaa3065e307002fa","True","ShadowCaster","0","2","ShadowCaster","0","False","True","1","1","False","","0","False","","1","1","False","","0","False","","True","1","False","","1","False","","False","False","False","False","False","False","False","False","False","True","0","False","","False","True","0","False","","False","True","True","True","True","True","0","False","","False","False","False","False","False","False","False","True","False","0","False","","255","False","","255","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","False","True","1","False","","False","True","True","0","False","","0","False","","True","1","False","","True","1","RenderType=Opaque=RenderType","True","3","True","14","all","0","False","False","False","False","False","False","False","False","False","False","False","False","True","0","False","","False","False","False","False","False","False","False","False","False","False","False","False","False","True","1","False","","True","3","False","","False","False","True","1","LightMode=ShadowCaster","False","False","0","","0","0","Standard","0","True","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":1193,"pos":[-896,-6784],"params":["Inherit","False","3583.845","100","Final","0","FINAL OUTPUT","0.4980392,1,0,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":914,"pos":[1792,-128],"params":["Inherit","False","515","100","Cloud Colors","0","","0,0.4980392,1,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":991,"pos":[2176,-1152],"params":["Inherit","False","517","100","Sun Texture, Color and Intensity","0","","0,0.4980392,1,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":480,"pos":[640,-3072],"params":["Inherit","False","774.4041","100","Color Gradient Colors","0","","0,0.4980392,1,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":1159,"pos":[384,-1920],"params":["Inherit","False","273","100","Stars Cubemap","0","","0,0.4980392,1,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":1026,"pos":[2816,-768],"params":["Inherit","False","1028.332","100","Direction Negative Z Mask","0","","1,0,0,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":952,"pos":[1024,-1920],"params":["Inherit","False","3200.168","100","Stars Horizon Height Mask","0","","1,0,0,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":1188,"pos":[-896,-7424],"params":["Inherit","False","1276.25","100","Drawers","0","","1,0.252,0,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":1195,"pos":[-896,-3072],"params":["Inherit","False","1406.953","100","Color Gradient Calculation","0","GRADIENT","0,0.4980392,0,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":1154,"pos":[-896,-128],"params":["Inherit","False","1791.123","100","Clouds Coordinates","0","CLOUDS","0,0.4980392,0,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":1006,"pos":[-896,-1152],"params":["Inherit","False","2940.454","100","Calculate Sun Position","0","SUN","0,0.4980392,0,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":1194,"pos":[-896,-5376],"params":["Inherit","False","771","100","Switch between Perspective / Orthographic camera","0","CAMERA MODE","1,0,1,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":1155,"pos":[1024,-128],"params":["Inherit","False","579","100","Clouds Cubemap","0","","0,0.4980392,1,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":1157,"pos":[-896,-1920],"params":["Inherit","False","1150","100","Stars Cubemaps Coords","0","STARS","0,0.4980392,0,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":700,"pos":[-896,768],"params":["Inherit","False","1415.783","100","Fog Coords on Screen","0","BUILT-IN FOG","0,0.4980392,0,1","0","0"]}
{"wire":[1258,0,1259,0]}
{"wire":[1237,0,1236,0]}
{"wire":[1256,0,1257,0]}
{"wire":[1256,1,1258,0]}
{"wire":[1256,2,1260,0]}
{"wire":[309,0,267,2]}
{"wire":[309,1,267,1]}
{"wire":[1239,1,1237,0]}
{"wire":[1255,0,1256,0]}
{"wire":[1005,0,1255,0]}
{"wire":[268,0,1007,0]}
{"wire":[268,1,309,0]}
{"wire":[268,2,267,4]}
{"wire":[1216,0,1241,0]}
{"wire":[1251,0,1211,0]}
{"wire":[1240,0,1238,0]}
{"wire":[1240,1,1239,0]}
{"wire":[962,0,1255,0]}
{"wire":[962,1,961,0]}
{"wire":[300,0,268,0]}
{"wire":[964,0,1005,0]}
{"wire":[964,1,962,0]}
{"wire":[1217,0,1216,0]}
{"wire":[1252,38,1240,0]}
{"wire":[1252,41,1212,0]}
{"wire":[1252,43,1251,0]}
{"wire":[967,0,962,0]}
{"wire":[965,0,964,0]}
{"wire":[1241,0,1252,19]}
{"wire":[1223,0,1222,0]}
{"wire":[968,0,967,0]}
{"wire":[968,1,1176,0]}
{"wire":[969,0,965,0]}
{"wire":[969,1,1176,0]}
{"wire":[673,0,1223,1]}
{"wire":[673,1,674,0]}
{"wire":[970,0,968,0]}
{"wire":[970,1,969,0]}
{"wire":[675,0,1223,0]}
{"wire":[675,1,673,0]}
{"wire":[675,2,1223,2]}
{"wire":[828,0,1175,2]}
{"wire":[832,0,828,0]}
{"wire":[832,1,1017,0]}
{"wire":[832,2,831,0]}
{"wire":[832,3,1171,0]}
{"wire":[832,4,1018,0]}
{"wire":[972,0,970,0]}
{"wire":[1165,0,675,0]}
{"wire":[976,0,972,1]}
{"wire":[976,1,971,0]}
{"wire":[287,0,1221,1]}
{"wire":[763,0,1165,0]}
{"wire":[822,0,832,0]}
{"wire":[1243,1,673,0]}
{"wire":[980,0,972,0]}
{"wire":[980,1,976,0]}
{"wire":[1246,2,975,0]}
{"wire":[278,0,241,0]}
{"wire":[278,1,1090,0]}
{"wire":[1221,0,1220,0]}
{"wire":[1254,6,287,0]}
{"wire":[1254,8,212,0]}
{"wire":[1230,0,1243,0]}
{"wire":[564,1,763,0]}
{"wire":[1166,0,822,0]}
{"wire":[985,0,980,0]}
{"wire":[985,1,1246,0]}
{"wire":[244,0,1225,1]}
{"wire":[244,1,278,0]}
{"wire":[208,0,1254,0]}
{"wire":[475,0,471,0]}
{"wire":[1244,1,1221,1]}
{"wire":[626,0,564,2]}
{"wire":[626,1,619,0]}
{"wire":[1225,0,1224,0]}
{"wire":[856,0,1166,0]}
{"wire":[988,0,1028,0]}
{"wire":[988,1,1177,0]}
{"wire":[1247,0,985,0]}
{"wire":[1151,0,244,0]}
{"wire":[1151,1,1152,0]}
{"wire":[470,0,208,0]}
{"wire":[470,1,475,0]}
{"wire":[303,0,1244,0]}
{"wire":[876,0,856,0]}
{"wire":[876,1,626,0]}
{"wire":[994,0,988,0]}
{"wire":[1168,0,1247,0]}
{"wire":[246,0,1225,0]}
{"wire":[246,1,1151,0]}
{"wire":[246,2,1225,2]}
{"wire":[1227,0,1226,0]}
{"wire":[1233,0,1231,0]}
{"wire":[180,0,194,5]}
{"wire":[180,1,182,5]}
{"wire":[180,2,303,0]}
{"wire":[417,0,470,0]}
{"wire":[1199,1,1219,0]}
{"wire":[886,0,876,0]}
{"wire":[993,0,1168,0]}
{"wire":[1169,0,994,0]}
{"wire":[1163,0,246,0]}
{"wire":[314,0,1227,1]}
{"wire":[1234,1,1233,0]}
{"wire":[1234,2,1232,0]}
{"wire":[1267,0,629,0]}
{"wire":[1201,0,1199,5]}
{"wire":[1201,1,1200,0]}
{"wire":[288,0,181,5]}
{"wire":[288,1,180,0]}
{"wire":[288,2,417,0]}
{"wire":[882,0,886,0]}
{"wire":[882,1,1267,0]}
{"wire":[882,2,1234,0]}
{"wire":[1249,0,996,0]}
{"wire":[1250,0,996,0]}
{"wire":[995,1,993,0]}
{"wire":[997,0,1169,0]}
{"wire":[1133,0,1163,0]}
{"wire":[329,0,325,0]}
{"wire":[1253,6,314,0]}
{"wire":[1253,8,313,0]}
{"wire":[1266,0,1261,5]}
{"wire":[1266,1,629,0]}
{"wire":[1202,0,1201,0]}
{"wire":[218,0,288,0]}
{"wire":[630,0,882,0]}
{"wire":[1263,0,1266,0]}
{"wire":[1002,0,995,1]}
{"wire":[1002,1,998,5]}
{"wire":[1002,2,1250,0]}
{"wire":[1001,0,995,4]}
{"wire":[1001,1,1249,0]}
{"wire":[1001,2,997,0]}
{"wire":[41,1,1133,0]}
{"wire":[677,0,1253,0]}
{"wire":[677,1,329,0]}
{"wire":[1206,0,1205,0]}
{"wire":[1206,1,1204,0]}
{"wire":[1004,0,1002,0]}
{"wire":[1003,0,1001,0]}
{"wire":[1228,0,41,4]}
{"wire":[1228,1,1229,0]}
{"wire":[101,0,232,5]}
{"wire":[101,1,261,5]}
{"wire":[101,2,41,5]}
{"wire":[316,0,677,0]}
{"wire":[1207,1,1205,0]}
{"wire":[1207,0,1204,0]}
{"wire":[1207,2,1206,0]}
{"wire":[1264,0,1207,0]}
{"wire":[1264,1,1265,0]}
{"wire":[1264,2,632,0]}
{"wire":[222,0,101,0]}
{"wire":[223,0,1228,0]}
{"wire":[678,0,316,0]}
{"wire":[678,1,1110,0]}
{"wire":[678,2,679,0]}
{"wire":[1170,1,1207,0]}
{"wire":[1170,0,1264,0]}
{"wire":[176,0,1170,0]}
{"wire":[176,1,1112,0]}
{"wire":[176,2,1111,0]}
{"wire":[359,0,678,0]}
{"wire":[1167,1,1170,0]}
{"wire":[1167,0,176,0]}
{"wire":[227,0,1167,0]}
{"wire":[227,1,228,0]}
{"wire":[227,2,229,0]}
{"wire":[1162,1,1167,0]}
{"wire":[1162,0,227,0]}
{"wire":[317,0,312,0]}
{"wire":[317,1,1162,0]}
{"wire":[317,2,436,0]}
{"wire":[921,1,1162,0]}
{"wire":[921,0,317,0]}
{"wire":[1197,0,921,0]}
ASEEND*/
//CHKSM=4B16C07C1872811118B41B353CA13A15671F5D26