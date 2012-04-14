/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Communications;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using OpenSim.Tests.Common.Mock;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// Spatial scene object tests (will eventually cover root and child part position, rotation properties, etc.)
    /// </summary>
    [TestFixture]
    public class SceneObjectSpatialTests
    {
        TestScene m_scene;
        UUID m_ownerId = TestHelpers.ParseTail(0x1);

        [SetUp]
        public void SetUp()
        {
            m_scene = SceneHelpers.SetupScene();
        }

        [Test]
        public void TestGetSceneObjectGroupPosition()
        {
            TestHelpers.InMethod();

            Vector3 position = new Vector3(10, 20, 30);

            SceneObjectGroup so
                = SceneHelpers.CreateSceneObject(1, m_ownerId, "obj1", 0x10);
            so.AbsolutePosition = position;
            m_scene.AddNewSceneObject(so, false);

            Assert.That(so.AbsolutePosition, Is.EqualTo(position));
        }

        [Test]
        public void TestGetRootPartPosition()
        {
            TestHelpers.InMethod();

            Vector3 partPosition = new Vector3(10, 20, 30);

            SceneObjectGroup so
                = SceneHelpers.CreateSceneObject(1, m_ownerId, "obj1", 0x10);
            so.AbsolutePosition = partPosition;
            m_scene.AddNewSceneObject(so, false);

            Assert.That(so.RootPart.AbsolutePosition, Is.EqualTo(partPosition));
            Assert.That(so.RootPart.GroupPosition, Is.EqualTo(partPosition));
            Assert.That(so.RootPart.GetWorldPosition(), Is.EqualTo(partPosition));
            Assert.That(so.RootPart.RelativePosition, Is.EqualTo(partPosition));
            Assert.That(so.RootPart.OffsetPosition, Is.EqualTo(Vector3.Zero));
        }
    }
}