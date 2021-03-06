﻿using MetroDom.Core;
using Sanford.Multimedia;
using System;
using System.Collections.Generic;
using Xunit;

namespace MetroDom.Tests.Core
{
    public class OrchestraTests
    {
        [Fact]
        public void Start_IsSuccessful_WithNullInstruments()
        {
            // Arrange
            var orchestra = new Orchestra(null);
            var song = new Song(Key.EMajor, new List<SongNote> { new SongNote(MetroDom.Core.Note.C, 500) });

            // Act
            orchestra.Start(song);

            // Assert
        }

        [Fact]
        public void Start_ThrowsArgumentNullException_WithNullSong()
        {
            // Arrange
            var orchestra = new Orchestra(null);

            // Act        
            var exception = Record.Exception(() => orchestra.Start(null));

            // Assert
            Assert.NotNull(exception);
            Assert.IsType<ArgumentNullException>(exception);
        }

    }
}
