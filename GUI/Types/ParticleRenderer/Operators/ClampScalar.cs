using System;
using GUI.Utils;
using ValveResourceFormat;
using ValveResourceFormat.Serialization;

namespace GUI.Types.ParticleRenderer.Operators
{
    class ClampScalar : IParticleOperator
    {
        private readonly INumberProvider outputMin;
        private readonly INumberProvider outputMax = new LiteralNumberProvider(1);
        private readonly ParticleField field = ParticleField.Radius;

        public ClampScalar(IKeyValueCollection keyValues)
        {
            if (keyValues.ContainsKey("m_nOutputField"))
            {
                field = keyValues.GetParticleField("m_nOutputField");
            }

            if (keyValues.ContainsKey("m_flOutputMin"))
            {
                outputMin = keyValues.GetNumberProvider("m_flOutputMin");
            }

            if (keyValues.ContainsKey("m_flOutputMax"))
            {
                outputMax = keyValues.GetNumberProvider("m_flOutputMax");
            }
        }

        public void Update(Span<Particle> particles, float frameTime, ParticleSystemRenderState particleSystemState)
        {
            foreach (ref var particle in particles)
            {
                var clampedValue = Math.Clamp(particle.GetScalar(field),
                    outputMin.NextNumber(ref particle, particleSystemState),
                    outputMax.NextNumber(ref particle, particleSystemState)
                );
                particle.SetScalar(field, clampedValue);
            }
        }
    }
}