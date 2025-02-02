#version 460

in vec3 aVertexPosition;

#include "common/ViewConstants.glsl"
uniform mat4 uModelMatrix;

out vec2 uv;

void main(void) {
    uv = aVertexPosition.xy * 0.5 + 0.5;
    gl_Position = g_matViewToProjection * uModelMatrix * vec4(aVertexPosition, 1.0);
}
