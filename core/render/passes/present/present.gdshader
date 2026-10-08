shader_type spatial;
render_mode unshaded, fog_disabled, cull_disabled, depth_draw_always, shadows_disabled;

// Fullscreen quad: the raymarched image and its depth; sky goes to the far plane (no discard, keeps antialiased edges).
uniform sampler2D color_tex : filter_linear;
uniform sampler2D depth_tex : filter_nearest;
// Sharpened after the history (in it, sharpening would build up); clamped to the 4 neighbours: no halos.
uniform float sharpness : hint_range(0.0, 1.0);

void vertex() {
	POSITION = vec4(VERTEX.xy * 2.0, 1.0, 1.0);
}

void fragment() {
	float z = texture(depth_tex, SCREEN_UV).r;
	vec2 px = 1.0 / vec2(textureSize(color_tex, 0));
	vec3 e = texture(color_tex, SCREEN_UV).rgb;
	vec3 b = texture(color_tex, SCREEN_UV - vec2(0.0, px.y)).rgb, h = texture(color_tex, SCREEN_UV + vec2(0.0, px.y)).rgb;
	vec3 d = texture(color_tex, SCREEN_UV - vec2(px.x, 0.0)).rgb, f = texture(color_tex, SCREEN_UV + vec2(px.x, 0.0)).rgb;
	ALBEDO = clamp(e + sharpness * (e - 0.25 * (b + d + f + h)), min(e, min(min(b, d), min(f, h))), max(e, max(max(b, d), max(f, h))));
	vec4 clip = PROJECTION_MATRIX * vec4(0.0, 0.0, -z, 1.0);
	DEPTH = z > 0.0 ? clip.z / clip.w : 1e-7;
}
