shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_always, shadows_disabled;

// The mesh carries the block colour and texture in the vertex colours (like the model voxels); no atlas sampling.
// Lit by the frame's flat light (ambient + sun, Renderer.SunLight) so a face reads as bright as the lit world, not grey.
uniform vec3 light = vec3(1.0);
uniform float alpha = 1.0;
uniform float bias = 0.0004;

void vertex() {
	POSITION = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	POSITION.z += bias * POSITION.w; // nudge toward the camera so the ghost does not z-fight with world faces at the same depth
}

void fragment() {
	ALBEDO = pow(COLOR.rgb, vec3(2.2)) * light;
	ALPHA = alpha;
}
