

namespace AI_Workshop03
{

    // MapDataGenerator.Reset.cs         -   Purpose: map-state clearing rules
    public sealed partial class MapDataGenerator
    {

        private void ResetToBase()
        {
            EnsureGenBuffers();

            for (int i = 0; i < _cellCount; i++)
            {
                _blocked[i] = false;
                _terrainKey[i] = (byte)TerrainID.Land;
                _terrainCost[i] = _baseWalkableCost;
                _baseColors[i] = _baseWalkableColor;
                _lastPaintLayerId[i] = 0;
            }

            _blockedCount = 0;
        }

        private void ResetWalkableToBaseOnly()
        {
            for (int i = 0; i < _cellCount; i++)
            {
                if (_blocked[i]) continue;

                _terrainKey[i] = (byte)TerrainID.Land;
                _terrainCost[i] = _baseWalkableCost;
                _baseColors[i] = _baseWalkableColor;
                _lastPaintLayerId[i] = 0;
            }
        }

        private int CountWalkable()
        {
            int count = 0;
            for (int i = 0; i < _cellCount; i++)
                if (!_blocked[i]) count++;
            return count;
        }


    }

}